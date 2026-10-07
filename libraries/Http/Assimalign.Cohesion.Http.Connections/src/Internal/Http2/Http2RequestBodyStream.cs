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
/// </remarks>
internal sealed class Http2RequestBodyStream : Stream
{
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

    private ReadOnlyMemory<byte> _current;
    private int _currentFlowControlDebt;
    private bool _completed;

    public Http2RequestBodyStream(
        ChannelReader<Http2DataChunk> reader,
        Func<int, int, CancellationToken, ValueTask> onConsumed,
        int streamId,
        CancellationToken requestAborted,
        Action onEndOfBody)
    {
        _reader = reader;
        _onConsumed = onConsumed;
        _streamId = streamId;
        _requestAborted = requestAborted;
        _onEndOfBody = onEndOfBody;
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

        while (true)
        {
            // RFC 9113 §5.4.2 — a reset stream aborts the application's view of the
            // request. Surface it as cancellation before delivering any further
            // bytes so a handler mid-read unwinds rather than seeing a clean EOF.
            _requestAborted.ThrowIfCancellationRequested();
            cancellationToken.ThrowIfCancellationRequested();

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
