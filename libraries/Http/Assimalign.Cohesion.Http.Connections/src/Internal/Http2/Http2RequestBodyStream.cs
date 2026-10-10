using System;
using System.IO;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Http.Connections.Internal;

/// <summary>
/// A read-only <see cref="Stream"/> over the inbound DATA of a single HTTP/2
/// stream. The frame pump writes decoded <see cref="Http2DataChunk"/>s to the
/// backing channel as they arrive; the application reads them here, and each
/// fully-consumed chunk credits its flow-control cost back to the peer through
/// the connection's consume callback.
/// </summary>
/// <remarks>
/// <para>
/// This is the consumption seam that turns HTTP/2 request-body flow control from
/// receipt-driven into application-driven (RFC 9113 §5.2). Because the peer's
/// send window is only replenished as the application drains this stream, a slow
/// reader applies real end-to-end backpressure: once the receive window is
/// exhausted a conformant sender stalls until the application reads more, and the
/// per-stream buffered bytes never exceed the advertised
/// <c>SETTINGS_INITIAL_WINDOW_SIZE</c>.
/// </para>
/// <para>
/// The stream is single-reader by contract — it is handed to exactly one request
/// handler. A peer <c>RST_STREAM</c> (or a local reset) fires
/// <see cref="Http2Stream.RequestAborted"/>, which surfaces here as an
/// <see cref="OperationCanceledException"/> so a handler blocked reading the body
/// wakes up and unwinds. The stream fires that abort before it fails the pipe with an
/// <see cref="IOException"/>, so a body cut off by a reset or by the loss of the
/// connection never reads as a clean end: only the peer's END_STREAM ends the body
/// with a 0-octet read (RFC 9113 §8.1, #1327).
/// </para>
/// <para>
/// Reaching the clean end of the body is also when the request's trailer section, if
/// it carried one, appears on <c>Request.Trailers</c>: the frame pump decodes the
/// section before it completes the pipe, and this stream publishes it at the end.
/// </para>
/// <para>
/// <b>Minimum data rate (#1085).</b> When the connection carries an
/// <see cref="Http2RequestBodyDataRate"/>, the body is held to it as HTTP/1.1 holds its own:
/// a <see cref="MinDataRateGate"/> started at the first read charges only the time this
/// reader waits for DATA the pipe does not yet hold, and every octet delivered extends the
/// allowance. A wait is not charged while the connection-level receive window is low, since
/// the peer may then be unable to send because other streams hold the window. A body that
/// falls below the rate is answered by the connection — <c>408</c> while no response has
/// started, a reset otherwise — and the read fails with an <see cref="IOException"/>.
/// </para>
/// </remarks>
internal sealed class Http2RequestBodyStream : Stream
{
    // While the connection's receive window is low, the reader's waits are not charged against the
    // rate, and a wait whose allowance is spent or nearly spent is bounded by this instead, so the
    // reader re-checks the window without spinning.
    private static readonly TimeSpan _backpressureRecheckInterval = TimeSpan.FromSeconds(1);

    private readonly ChannelReader<Http2DataChunk> _reader;
    // Invoked as each chunk is fully consumed to credit its flow-control cost
    // back to the peer — (streamId, flowControlLength, cancellationToken).
    // A plain delegate rather than a one-method interface: the connection context
    // is the only implementer, matching how the HTTP/1.1 reader takes its
    // timeout-phase signals as delegates.
    private readonly Func<int, int, CancellationToken, ValueTask> _onConsumed;
    // Invoked once, when the reader reaches the clean end of the body: the stream publishes the
    // request's trailer section there (RFC 9110 §6.5), as HTTP/1.1 and HTTP/3 do.
    private readonly Action _onEndOfBody;
    private readonly int _streamId;
    private readonly CancellationToken _requestAborted;
    // The connection's minimum request-body data rate, or null when none is configured or the stream is
    // a CONNECT tunnel. The gate that enforces it starts at the first read.
    private readonly Http2RequestBodyDataRate? _dataRate;

    private ReadOnlyMemory<byte> _current;
    private int _currentFlowControlDebt;
    private bool _completed;
    private MinDataRateGate? _gate;
    private IOException? _rateFailure;

    /// <summary>
    /// Initializes the body over a stream's DATA pipe.
    /// </summary>
    /// <param name="reader">The pipe the frame pump writes the stream's DATA to.</param>
    /// <param name="onConsumed">Credits a consumed chunk's flow-control cost back to the peer.</param>
    /// <param name="streamId">The stream identifier.</param>
    /// <param name="requestAborted">The stream's abort token, fired when it is reset.</param>
    /// <param name="onEndOfBody">Publishes the trailer section at the clean end of the body.</param>
    /// <param name="dataRate">
    /// The connection's minimum request-body data rate, or <see langword="null"/> to leave the body
    /// unbounded in time (no rate configured, or a CONNECT tunnel).
    /// </param>
    public Http2RequestBodyStream(
        ChannelReader<Http2DataChunk> reader,
        Func<int, int, CancellationToken, ValueTask> onConsumed,
        int streamId,
        CancellationToken requestAborted,
        Action onEndOfBody,
        Http2RequestBodyDataRate? dataRate = null)
    {
        _reader = reader;
        _onConsumed = onConsumed;
        _streamId = streamId;
        _requestAborted = requestAborted;
        _onEndOfBody = onEndOfBody;
        _dataRate = dataRate;
    }

    /// <inheritdoc />
    public override bool CanRead => true;

    /// <inheritdoc />
    public override bool CanSeek => false;

    /// <inheritdoc />
    public override bool CanWrite => false;

    /// <inheritdoc />
    public override long Length => throw new NotSupportedException("The HTTP/2 request body length is not known in advance.");

    /// <inheritdoc />
    public override long Position
    {
        get => throw new NotSupportedException("The HTTP/2 request body stream is not seekable.");
        set => throw new NotSupportedException("The HTTP/2 request body stream is not seekable.");
    }

    /// <inheritdoc />
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (buffer.IsEmpty)
        {
            return 0;
        }

        // The transport consumes the body from here on: the rate is enforced from the first read, as on
        // HTTP/1.1, so a peer that holds its body back gets the grace period and no more.
        if (_dataRate is not null)
        {
            _gate ??= new MinDataRateGate(_dataRate.Rate, _dataRate.TimeProvider);
        }

        while (true)
        {
            // RFC 9113 §5.4.2 — a reset stream aborts the application's view of the
            // request. Surface it as cancellation before delivering any further
            // bytes so a handler mid-read unwinds rather than seeing a clean EOF.
            _requestAborted.ThrowIfCancellationRequested();
            cancellationToken.ThrowIfCancellationRequested();

            if (_rateFailure is not null)
            {
                throw _rateFailure;
            }

            if (!_current.IsEmpty)
            {
                int take = Math.Min(_current.Length, buffer.Length);
                _current.Span.Slice(0, take).CopyTo(buffer.Span);
                _current = _current.Slice(take);

                if (_current.IsEmpty)
                {
                    await CreditCurrentAsync(cancellationToken).ConfigureAwait(false);
                }

                return take;
            }

            if (_completed)
            {
                return 0;
            }

            if (!await TryLoadNextChunkAsync(cancellationToken).ConfigureAwait(false))
            {
                return 0;
            }
        }
    }

    /// <inheritdoc />
    public override int Read(byte[] buffer, int offset, int count)
    {
        ValidateBufferArguments(buffer, offset, count);
        return ReadAsync(buffer.AsMemory(offset, count), CancellationToken.None).AsTask().GetAwaiter().GetResult();
    }

    /// <inheritdoc />
    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        ValidateBufferArguments(buffer, offset, count);
        return ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
    }

    /// <inheritdoc />
    public override void Flush()
    {
        // A request body is read-only; there is nothing to flush.
    }

    /// <inheritdoc />
    public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <inheritdoc />
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException("The HTTP/2 request body stream is not seekable.");

    /// <inheritdoc />
    public override void SetLength(long value) => throw new NotSupportedException("The HTTP/2 request body stream is read-only.");

    /// <inheritdoc />
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException("The HTTP/2 request body stream is read-only.");

    /// <summary>
    /// Loads the next chunk from the channel, crediting any padding-only chunk
    /// immediately (it carries flow-control cost but no application data). Returns
    /// <see langword="false"/> once the channel has been completed and drained.
    /// </summary>
    private async ValueTask<bool> TryLoadNextChunkAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            if (_reader.TryRead(out Http2DataChunk chunk))
            {
                _current = chunk.Data;
                _currentFlowControlDebt = chunk.FlowControlLength;

                // Every octet the peer delivered extends its allowance, whenever it arrived.
                _gate?.Record(ticksWaited: 0, bytesTransferred: chunk.Data.Length);

                if (_current.IsEmpty)
                {
                    // A padding-only DATA frame carries flow-control cost but no
                    // application data — credit it now and look for the next chunk.
                    await CreditCurrentAsync(cancellationToken).ConfigureAwait(false);
                    continue;
                }

                return true;
            }

            if (!await WaitToReadAsync(cancellationToken).ConfigureAwait(false))
            {
                // The pipe completed cleanly. A trailer section, when the request carried one, was
                // decoded and validated before the pump completed the pipe, so it is complete now.
                _completed = true;
                _onEndOfBody();
                return false;
            }
        }
    }

    /// <summary>
    /// Awaits more data, honoring both the caller's token and the request-abort
    /// token so a peer reset unblocks a pending read.
    /// </summary>
    private async ValueTask<bool> WaitToReadAsync(CancellationToken cancellationToken)
    {
        if (_gate is not null)
        {
            return await WaitToReadWithinRateAsync(_gate, cancellationToken).ConfigureAwait(false);
        }

        if (!_requestAborted.CanBeCanceled)
        {
            return await _reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false);
        }

        using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _requestAborted);
        try
        {
            return await _reader.WaitToReadAsync(linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_requestAborted.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            // The stream was reset while we waited — re-throw against the abort
            // token so callers observe the request-abort, not a caller cancel.
            throw new OperationCanceledException(_requestAborted);
        }
    }

    /// <summary>
    /// Awaits more data within the body's minimum data rate (#1085). Each wait is bounded by what is
    /// left of the peer's allowance and, unless the connection's receive window was low while it ran, is
    /// charged against it. When the allowance is spent the connection answers the stream and the read
    /// fails.
    /// </summary>
    private async ValueTask<bool> WaitToReadWithinRateAsync(MinDataRateGate gate, CancellationToken cancellationToken)
    {
        Http2RequestBodyDataRate dataRate = _dataRate!;
        TimeProvider clock = gate.TimeProvider;

        while (true)
        {
            bool backpressured = dataRate.IsConnectionReceiveBackpressured;
            bool withinAllowance = gate.TryGetOperationTimeout(out TimeSpan timeout);

            if (!withinAllowance && !backpressured)
            {
                throw await RejectTooSlowAsync(dataRate).ConfigureAwait(false);
            }

            if (backpressured && (!withinAllowance || timeout < _backpressureRecheckInterval))
            {
                timeout = _backpressureRecheckInterval;
            }

            using CancellationTokenSource timeoutSource = new(timeout, clock);
            using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _requestAborted, timeoutSource.Token);

            long start = clock.GetTimestamp();

            try
            {
                bool more = await _reader.WaitToReadAsync(linked.Token).ConfigureAwait(false);
                Charge(gate, dataRate, start, backpressured);
                return more;
            }
            catch (OperationCanceledException) when (timeoutSource.IsCancellationRequested
                && !cancellationToken.IsCancellationRequested
                && !_requestAborted.IsCancellationRequested)
            {
                // The allowance (or the backpressure re-check interval) ran out with no DATA. Charge the
                // wait and look again: the next pass rejects the body if the allowance is spent.
                Charge(gate, dataRate, start, backpressured);
            }
            catch (OperationCanceledException) when (_requestAborted.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
            {
                throw new OperationCanceledException(_requestAborted);
            }
        }
    }

    /// <summary>
    /// Charges a finished wait against the rate, unless the connection's receive window was low when
    /// it began or ended: the peer may then have had no window to send in.
    /// </summary>
    private static void Charge(MinDataRateGate gate, Http2RequestBodyDataRate dataRate, long start, bool backpressuredAtStart)
    {
        long waited = gate.TimeProvider.GetTimestamp() - start;

        if (!backpressuredAtStart && !dataRate.IsConnectionReceiveBackpressured)
        {
            gate.Record(waited, bytesTransferred: 0);
        }
    }

    /// <summary>
    /// Latches the rejection of a body that fell below the minimum data rate, has the connection answer
    /// the stream (RFC 9110 §15.5.9 — <c>408</c> while no response has started), and returns the failure
    /// the read throws. Every later read throws it too, unless the stream's reset surfaces first.
    /// </summary>
    private async ValueTask<IOException> RejectTooSlowAsync(Http2RequestBodyDataRate dataRate)
    {
        _rateFailure = new IOException(
            $"The HTTP/2 request body on stream {_streamId} was received below the configured minimum data rate of {dataRate.Rate.BytesPerSecond} octets per second (408 Request Timeout).");

        await dataRate.RejectTooSlowAsync(_streamId).ConfigureAwait(false);
        return _rateFailure;
    }

    private async ValueTask CreditCurrentAsync(CancellationToken cancellationToken)
    {
        int debt = _currentFlowControlDebt;
        _currentFlowControlDebt = 0;

        if (debt > 0)
        {
            await _onConsumed(_streamId, debt, cancellationToken).ConfigureAwait(false);
        }
    }
}
