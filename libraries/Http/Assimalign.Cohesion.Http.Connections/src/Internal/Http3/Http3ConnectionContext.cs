using System;
using System.Globalization;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipelines;
using System.Net;
using System.Net.Quic;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

using Assimalign.Cohesion.Connections;

namespace Assimalign.Cohesion.Http.Connections.Internal;

internal sealed partial class Http3ConnectionContext : HttpConnectionContext
{
    // These diagnostics also run during ordinary validation, cancellation or completion.
    private static readonly string _requestDispatchStoppedMessage = ErrorMessages.ConnectionStoppedBeforeTheRequestWasDispatched;
    private static readonly string _exchangeCancelledMessage = ErrorMessages.ExchangeWasCancelled;

    // RFC 9114 §7.2.4 / RFC 9218 §7.2 — the peer's SETTINGS frame and its control-stream PRIORITY_UPDATE
    // frames are read whole before they are applied. Both carry a handful of varints, so a declared
    // length beyond this bound is refused rather than buffered.
    private const int maxControlFramePayloadSize = 16 * 1024;

    private readonly IMultiplexedConnection _connection;
    private readonly bool _isSecure;
    // The QUIC handshake's facts, published on every request stream's connection info as the
    // ITlsConnectionInfo facet; null when the connection does not report them (see HttpTlsConnectionInfo).
    private readonly ITlsConnectionInfo? _tls;
    private readonly IMultiplexedConnectionStreamLimits? _streamLimits;
    private readonly Http3QPackOptions _qpackOptions;
    private readonly Http3PeerSettings _peerSettings = new();
    // Cancelled when the receive enumeration tears down — the consumer ended it, or a connection error
    // was raised. Only work that exists to feed the enumeration observes it: the accept loop and the
    // head reads of requests not yet dispatched. Everything an exchange still needs after the
    // enumeration ends — its request-body reads, trailer decodes, the QPACK encoder drain that feeds
    // them, the control-stream drain — lives until the connection itself closes
    // (IMultiplexedConnection.ConnectionClosed) instead. Deliberately never disposed: exchanges outlive
    // the enumeration, and a disposed source can no longer be read or linked.
    private readonly CancellationTokenSource _teardownSource = new();
    private readonly QPackDecoderState? _decoderState;
    // Serializes writes to the single outbound QPACK decoder stream. Several producers share it once
    // the dynamic table is enabled: the encoder-stream drain (Insert Count Increment) and every request
    // stream's processing (Section Acknowledgment / Stream Cancellation, keyed on the request stream
    // ID). A PipeWriter tolerates no concurrent writers, so every decoder instruction goes out under
    // this gate. Never disposed: request bodies write through it after the enumeration ends.
    private readonly SemaphoreSlim _decoderWriteGate = new(1, 1);
    // Requests whose HEADERS have decoded, in the order they became ready; the receive enumeration
    // yields from here. Unbounded on purpose: each entry is a request head the peer already holds a
    // QUIC stream open for, so QUIC's concurrent-stream limit bounds it, and a request body is never
    // buffered here (it is read lazily by Http3RequestBodyStream).
    private readonly Channel<Http3Context> _readyContexts = Channel.CreateUnbounded<Http3Context>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });
    // Accepted streams whose processing is still running, plus one reference held by the accept loop
    // itself. Whoever brings it to zero completes _streamWorkDrained, after which the accept loop
    // completes the ready-context channel — so no context is published after the enumeration ends.
    private int _pendingStreamWork = 1;
    private readonly TaskCompletionSource _streamWorkDrained = new(TaskCreationOptions.RunContinuationsAsynchronously);
    // The first connection error raised on this connection (Interlocked one-shot).
    private Http3ConnectionException? _connectionError;
    private volatile bool _localConnectionClose;
    private CancellationToken _receiveCancellation;
    private IMultiplexedConnectionStream? _controlStream;
    private IMultiplexedConnectionStream? _decoderStream;
    private Task? _acceptLoopTask;
    // Interlocked one-shot latches: unidirectional streams are typed concurrently, so "at most one
    // control / QPACK encoder / QPACK decoder stream" (RFC 9114 §6.2.1, RFC 9204 §4.2) is enforced
    // atomically.
    private int _controlStreamReceived;
    private int _qpackEncoderStreamReceived;
    private int _qpackDecoderStreamReceived;
    // RFC 9114 §5.2 — the number of client-initiated bidirectional request
    // streams this connection has accepted. At teardown the GOAWAY announces
    // the lowest stream ID the server will NOT process; with QUIC's
    // client-bidi numbering (0, 4, 8, …) that boundary is
    // (accepted count) × 4, so every stream already accepted (IDs below the
    // boundary) falls inside "may have been processed" while later streams
    // are rejected. Counted at accept — not at dispatch — so a malformed
    // stream the server touched and dropped is still inside the boundary and
    // the client will not retry a request whose side effects may have run.
    // Mutated with Interlocked from the accept loop; read by the dispose path.
    private int _processedRequestStreamCount;
    // Guards single GOAWAY emission across the receive-loop teardown and the
    // connection dispose path (Interlocked one-shot latch).
    private int _goAwaySent;
    // RFC 9114 §5.2 — a host's graceful close (BeginGracefulClose): one-shot, then the GOAWAY it
    // announces. The announcement waits on _acceptStopped, completed once the accept loop can accept
    // no further stream, so the boundary it carries counts every request stream ever accepted.
    private int _gracefulCloseStarted;
    private Task? _gracefulCloseAnnouncement;
    private readonly TaskCompletionSource _acceptStopped = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Http3ConnectionListenerOptions.Http3Limits _limits;
    private readonly IHttpExchangeInterceptor[] _requestInterceptors;
    private readonly IHttpExchangeInterceptor[] _responseInterceptors;
    // The number of features each exchange is expected to carry; its feature collection is sized
    // for them when the request stream's context is created.
    private readonly int _featureCapacity;
    // RFC 9218 §7.2 — the effective priority of request streams the peer has
    // re-prioritized via a control-stream PRIORITY_UPDATE. This is the HTTP/3
    // engine's observable priority state; response ordering across streams is
    // otherwise delegated to the QUIC transport (see docs/DESIGN.md). Guarded
    // because the control-stream drain runs on a background task.
    //
    // Bounded, because every PRIORITY_UPDATE naming a fresh ID would otherwise add an entry for the
    // connection's lifetime. A key is either a live request stream — accepted, its head or exchange not
    // yet ended — whose value stays null until an update names it, or an update buffered for a stream not
    // yet opened (at or above _priorityAcceptBoundary), at most maxBufferedPriorityUpdates of them. A
    // stream's entry goes when it ends, and an update for a stream that has ended is discarded.
    private const int maxBufferedPriorityUpdates = 128;
    private readonly Dictionary<long, HttpPriority?> _requestStreamPriorities = new();
    private readonly object _priorityLock = new();
    // The ID of the next request stream the accept loop takes: every ID below it was opened.
    private long _priorityAcceptBoundary;
    // How many keys of _requestStreamPriorities are updates buffered for streams not yet opened.
    private int _bufferedPriorityUpdates;

    // #1085 — the keep-alive deadline. The connection is idle while no request stream is in flight: none
    // is having its head read, and no exchange built from one is still running. _activeRequestStreams
    // counts them (a stream from acceptance until its head yields no exchange, or until its exchange
    // ends); _idleSince is when the receive loop started or the last exchange ended, whichever is later,
    // so a stream that never became an exchange does not move it. The timer, armed at zero for what is
    // left and re-checked when it fires, closes the connection gracefully once it has been idle for
    // KeepAliveTimeout. Guarded by
    // _keepAliveLock; the timer is created when the receive loop starts and disposed when it ends.
    // TimeProvider.System in production, as on HTTP/1.1.
    private readonly TimeProvider _timeProvider = TimeProvider.System;
    private readonly Lock _keepAliveLock = new();
    private TimeSpan _keepAliveTimeout;
    private ITimer? _keepAliveTimer;
    private int _activeRequestStreams;
    private long _idleSince;
    private bool _keepAliveStopped;

    private readonly HttpResponseHeaderPolicy _responseHeaderPolicy;

    [SupportedOSPlatform("windows")]
    [SupportedOSPlatform("linux")]
    [SupportedOSPlatform("macos")]
    [SupportedOSPlatform("osx")]
    public Http3ConnectionContext(
        IMultiplexedConnection connection,
        bool isSecure,
        Http3ConnectionListenerOptions.Http3Limits limits,
        IHttpExchangeInterceptor[] requestInterceptors,
        IHttpExchangeInterceptor[] responseInterceptors,
        int featureCapacity,
        Http3QPackOptions qpackOptions,
        HttpResponseHeaderPolicy? responseHeaderPolicy = null)
    {
        _connection = connection;
        _isSecure = isSecure;
        _tls = connection.Tls;
        _streamLimits = connection.StreamLimits;
        _limits = limits;
        _responseHeaderPolicy = responseHeaderPolicy ?? HttpResponseHeaderPolicy.Default;
        _requestInterceptors = requestInterceptors;
        _responseInterceptors = responseInterceptors;
        _featureCapacity = featureCapacity;
        // A copy, not the listener's live instance: the SETTINGS written when the receive loop starts, the
        // decoder state, and the static-only decode must all use the values this connection advertises,
        // even if the host changes the listener's options after the connection opens.
        _qpackOptions = qpackOptions.Snapshot();

        // The dynamic table (and its encoder/decoder instruction streams) is
        // opt-in: with QPACK_MAX_TABLE_CAPACITY = 0 the decoder state is never
        // created and the transport stays on the static-only path.
        _decoderState = _qpackOptions.DynamicTableEnabled ? new QPackDecoderState(_qpackOptions) : null;
    }

    public override EndPoint? LocalEndPoint => _connection.LocalEndPoint;
    public override EndPoint? RemoteEndPoint => _connection.RemoteEndPoint;

    /// <summary>
    /// Attempts to read the effective priority recorded for a request stream by a
    /// control-stream PRIORITY_UPDATE frame (RFC 9218 §7.2). Exposes the HTTP/3
    /// engine's observable priority state.
    /// </summary>
    /// <param name="streamId">The prioritized request-stream identifier.</param>
    /// <param name="priority">The recorded priority when present.</param>
    /// <returns><see langword="true"/> if a priority was recorded for the stream; otherwise <see langword="false"/>.</returns>
    internal bool TryGetRequestStreamPriority(long streamId, out HttpPriority priority)
    {
        lock (_priorityLock)
        {
            if (_requestStreamPriorities.TryGetValue(streamId, out HttpPriority? recorded) && recorded is { } value)
            {
                priority = value;
                return true;
            }
        }

        priority = default;
        return false;
    }

    /// <summary>
    /// Gets how many request streams the priority state holds an entry for: the live ones and the updates
    /// buffered for streams not yet opened. Exposes the state's bound to tests.
    /// </summary>
    internal int TrackedRequestStreamPriorityCount
    {
        get
        {
            lock (_priorityLock)
            {
                return _requestStreamPriorities.Count;
            }
        }
    }

    /// <summary>
    /// Records a request <c>PRIORITY_UPDATE</c> (RFC 9218 §7.2). An update for a live stream sets its
    /// priority, and one for a stream that has ended is discarded: it has nothing left to prioritize. An
    /// update for a stream not yet opened is buffered, the most recent per stream, for at most
    /// <see cref="maxBufferedPriorityUpdates"/> streams — the local bound RFC 9218 §7 allows — and dropped
    /// beyond it.
    /// </summary>
    private void RecordRequestStreamPriority(long streamId, HttpPriority priority)
    {
        lock (_priorityLock)
        {
            if (_requestStreamPriorities.ContainsKey(streamId))
            {
                _requestStreamPriorities[streamId] = priority;
            }
            else if (streamId >= _priorityAcceptBoundary && _bufferedPriorityUpdates < maxBufferedPriorityUpdates)
            {
                _requestStreamPriorities.Add(streamId, priority);
                _bufferedPriorityUpdates++;
            }
        }
    }

    /// <summary>
    /// The accept loop took request stream <paramref name="streamId"/>: it is live until it ends, and an update
    /// buffered for it now applies to it.
    /// </summary>
    private void OpenRequestStreamPriority(long streamId)
    {
        lock (_priorityLock)
        {
            _priorityAcceptBoundary = streamId + 4;

            if (!_requestStreamPriorities.TryAdd(streamId, null))
            {
                _bufferedPriorityUpdates--;
            }
        }
    }

    /// <summary>
    /// Request stream <paramref name="streamId"/> ended — its head yielded no exchange, or its exchange ended — so
    /// its priority is forgotten, and a later update for it is discarded.
    /// </summary>
    private void CloseRequestStreamPriority(long streamId)
    {
        lock (_priorityLock)
        {
            _requestStreamPriorities.Remove(streamId);
        }
    }

    /// <summary>
    /// Yields HTTP/3 request contexts for the lifetime of this connection.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Before accepting any inbound stream the server opens its own outbound
    /// unidirectional control stream and sends SETTINGS as the first frame
    /// (RFC 9114 §6.2.1). That control stream stays open for the connection's
    /// lifetime — it is a critical stream — and is torn down connection-first
    /// (see <see cref="ShutdownAsync"/>). SETTINGS emission is best-effort: if
    /// the QUIC connection is already gone, the accept loop observes the same
    /// failure and terminates.
    /// </para>
    /// <para>
    /// Inbound streams are processed concurrently. A background accept loop
    /// accepts each QUIC stream and hands it straight to processing of its own:
    /// a request stream is read only up to its HEADERS frame, QPACK-decoded, and
    /// published here as a context whose body is read lazily
    /// (<see cref="Http3RequestBodyStream"/>); a unidirectional stream is typed
    /// and, for the control and QPACK encoder streams, drained in the
    /// background. The accept loop never waits on a stream's contents, so a
    /// request whose HEADERS (or QPACK insertions) are still in flight, or whose
    /// body is still arriving, holds back no other stream. Contexts are yielded
    /// in the order their heads become ready.
    /// </para>
    /// <para>
    /// Failures are scoped per RFC 9114 §8. A stream error — a malformed request
    /// (<c>H3_MESSAGE_ERROR</c>), an oversized HEADERS frame
    /// (<c>H3_FRAME_ERROR</c>), a stream that ends without a request
    /// (<c>H3_REQUEST_INCOMPLETE</c>) — resets that stream and the connection
    /// keeps serving. A connection error — a truncated frame
    /// (<c>H3_FRAME_ERROR</c>), an invalid frame sequence or a prohibited frame
    /// (<c>H3_FRAME_UNEXPECTED</c>), a control-stream violation, a QPACK failure
    /// — aborts the connection; the enumeration ends after the contexts already
    /// published. The QUIC connection going away, disposal, cancellation, or a
    /// graceful close (<see cref="BeginGracefulClose"/>) end the enumeration
    /// cleanly. An unexpected exception (a programmer error, such
    /// as a request hook throwing something other than
    /// <see cref="HttpRequestRejectedException"/>) is not masked: it surfaces from
    /// the enumeration.
    /// </para>
    /// </remarks>
    public override async IAsyncEnumerable<IHttpContext> ReceiveAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        _receiveCancellation = cancellationToken;
        try
        {
            // #1085 — the connection is idle until its first request stream arrives.
            StartKeepAlive();

            // RFC 9114 §6.2.1 — each peer MUST open a control stream and send
            // SETTINGS as its first frame. Do this before (and independently
            // of) accepting request streams.
            await SendControlStreamSettingsAsync(cancellationToken).ConfigureAwait(false);

            _acceptLoopTask ??= RunAcceptLoopAsync(cancellationToken);

            while (await TryReadReadyContextAsync(cancellationToken).ConfigureAwait(false) is { } context)
            {
                yield return context;
            }
        }
        finally
        {
            await ShutdownAsync().ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Waits for the next ready request context. Returns <see langword="null"/> once the accept loop has
    /// completed the channel, or when the consumer's token is cancelled; a fault the accept loop
    /// forwarded through the channel is rethrown.
    /// </summary>
    private async ValueTask<Http3Context?> TryReadReadyContextAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (await _readyContexts.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
            {
                if (_readyContexts.Reader.TryRead(out Http3Context? context))
                {
                    return context;
                }
            }

            return null;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return null;
        }
    }

    /// <summary>
    /// The connection's accept loop. Accepts inbound QUIC streams until the connection ends, handing
    /// each to its own processing without waiting on it; once acceptance stops it waits for every
    /// started stream to finish its head processing, then completes the ready-context channel.
    /// </summary>
    private async Task RunAcceptLoopAsync(CancellationToken receiveToken)
    {
        Exception? fault = null;

        try
        {
            using CancellationTokenSource acceptCancellation =
                CancellationTokenSource.CreateLinkedTokenSource(receiveToken, _teardownSource.Token);
            CancellationToken cancellationToken = acceptCancellation.Token;

            while (!cancellationToken.IsCancellationRequested)
            {
                StreamAcceptOutcome accept = await TryAcceptInboundAsync(cancellationToken).ConfigureAwait(false);

                if (accept.TerminateConnection)
                {
                    break;
                }

                if (accept.StreamConnection is not { } streamConnection)
                {
                    // Non-terminating accept failure is not expected, but guard
                    // anyway: skip and try the next inbound.
                    continue;
                }

                Interlocked.Increment(ref _pendingStreamWork);

                // RFC 9114 §6 — a bidirectional stream is a request stream; the
                // peer's unidirectional streams carry a type prefix (control,
                // QPACK encoder/decoder, push) and are demultiplexed separately.
                // Neither is awaited here: each runs synchronously only until it
                // needs octets that have not arrived, then continues on its own.
                if (streamConnection.Direction == ConnectionDirection.Bidirectional)
                {
                    // RFC 9114 §5.2 — this bidirectional stream is now an accepted
                    // request stream; advance the boundary the teardown GOAWAY
                    // announces so it falls inside "may have been processed" whether
                    // it yields a context or is reset as malformed. The same
                    // client-bidi numbering law the GOAWAY boundary is derived from
                    // (RFC 9000 §2.1: IDs 0, 4, 8, … assigned in order, and a frame
                    // for a higher-numbered stream implicitly opens the lower ones
                    // first — §3.2 — so streams of a type surface in ascending order)
                    // also yields this stream's own wire ID: the k-th accepted
                    // request stream is stream 4(k−1). That ID keys the QPACK
                    // Section Acknowledgment / Stream Cancellation decoder
                    // instructions (RFC 9204 §4.4); capturing it off the same
                    // increment keeps the two derivations from ever drifting apart.
                    long requestStreamId = 4L * (Interlocked.Increment(ref _processedRequestStreamCount) - 1);

                    // RFC 9218 §7.2 — the stream is live for PRIORITY_UPDATE until it ends.
                    OpenRequestStreamPriority(requestStreamId);

                    // #1085 — the connection is busy until this stream's head yields no exchange, or
                    // the exchange it yields ends.
                    BeginRequestStream();
                    _ = ProcessRequestStreamAsync(streamConnection, requestStreamId, receiveToken);
                }
                else
                {
                    _ = ProcessUnidirectionalStreamAsync(streamConnection, receiveToken);
                }
            }

            // No request stream is accepted for processing after this point, so the request-stream count
            // is final; a graceful close waits for it before it announces its GOAWAY boundary.
            _acceptStopped.TrySetResult();

            // RFC 9114 §5.2 — a request stream the peer opens from here on is at or above that boundary.
            // It is still accepted, until the connection closes, and refused at once, so its client can
            // retry it elsewhere instead of waiting for the connection to close (#1306).
            if (!_connection.ConnectionClosed.IsCancellationRequested)
            {
                _ = RejectLateStreamsAsync();
            }

            // Release the loop's own reference, then wait for every stream it started: a head still
            // being read may yet publish a context, so the channel stays open until they are done.
            EndStreamWork();
            await _streamWorkDrained.Task.ConfigureAwait(false);
        }
        // Not swallowed: an unexpected fault (a programmer error) is forwarded to the consumer through
        // the channel, and the enumeration rethrows it.
        catch (Exception exception)
        {
            fault = exception;
        }
        finally
        {
            // Also when a fault ended the loop: nothing accepts afterwards.
            _acceptStopped.TrySetResult();
            _readyContexts.Writer.TryComplete(fault);
        }
    }

    private void EndStreamWork()
    {
        if (Interlocked.Decrement(ref _pendingStreamWork) == 0)
        {
            _streamWorkDrained.TrySetResult();
        }
    }

    /// <summary>
    /// Accepts the streams the peer opens once the accept loop has stopped — after a graceful close
    /// (<see cref="BeginGracefulClose"/>) or the end of the receive enumeration — until the connection
    /// closes (#1306). Never throws.
    /// </summary>
    /// <remarks>
    /// <para>
    /// RFC 9114 §5.2 — the request-stream count is final, so every request stream accepted here is at or
    /// above the boundary the <c>GOAWAY</c> announces: the server SHOULD cancel it. It is reset with
    /// <c>H3_REQUEST_REJECTED</c> (§4.1.1: not processed, so the client may retry it), without being read,
    /// counted toward the boundary, keeping the connection busy, or reaching the receive enumeration. Its
    /// wire ID continues the client-bidi numbering past the boundary, and keys the QPACK Stream
    /// Cancellation that the reset owes when the dynamic table is enabled (RFC 9204 §4.4.2).
    /// </para>
    /// <para>
    /// A unidirectional stream is typed and handled as before, against the connection's lifetime rather
    /// than the ended enumeration's, so a late critical stream is still read and its rules still hold.
    /// </para>
    /// </remarks>
    private async Task RejectLateStreamsAsync()
    {
        CancellationToken cancellationToken = _connection.ConnectionClosed;
        long rejectedCount = 0;

        while (true)
        {
            StreamAcceptOutcome accept = await TryAcceptInboundAsync(cancellationToken).ConfigureAwait(false);

            if (accept.TerminateConnection)
            {
                return;
            }

            if (accept.StreamConnection is not { } streamConnection)
            {
                continue;
            }

            if (streamConnection.Direction == ConnectionDirection.Bidirectional)
            {
                long requestStreamId = 4L * (Volatile.Read(ref _processedRequestStreamCount) + rejectedCount++);

                ResetRequestStream(
                    streamConnection,
                    requestStreamId,
                    new Http3RequestResetState(),
                    new Http3StreamException(
                        Http3ErrorCode.RequestRejected,
                        ErrorMessages.RequestStreamWasOpenedAfterTheConnectionBeganToCloseRfc9114),
                    abandonsReading: true);
            }
            else
            {
                _ = HandleUnidirectionalStreamAsync(streamConnection, cancellationToken);
            }
        }
    }

    /// <summary>
    /// Opens the server's outbound unidirectional control stream and writes the
    /// stream-type prefix (0x00) followed by a SETTINGS frame as its first frame
    /// (RFC 9114 §6.2.1 / §7.2.4). The stream is retained and deliberately left
    /// open — it is a critical stream (RFC 9114 §6.2.1) that must not be
    /// completed, aborted, or FIN'd until connection teardown, or a peer fails
    /// the connection with <c>H3_CLOSED_CRITICAL_STREAM</c>.
    /// </summary>
    /// <remarks>
    /// Best-effort: opening an outbound stream requires a live QUIC connection.
    /// If the connection is already gone the accept loop observes the same
    /// failure and terminates, so a setup failure here is swallowed rather than
    /// surfaced into the consumer's enumeration.
    /// </remarks>
    private async Task SendControlStreamSettingsAsync(CancellationToken cancellationToken)
    {
        if (_controlStream is not null)
        {
            return;
        }

        try
        {
            IMultiplexedConnectionStream controlStream = await _connection
                .OpenStreamAsync(ConnectionDirection.WriteOnly, cancellationToken)
                .ConfigureAwait(false);
            _controlStream = controlStream;

            // Write the control-stream preamble directly to the outbound pipe.
            // PipeWriter.WriteAsync flushes, so the SETTINGS frame reaches the
            // transport as the stream's first bytes. The output is not completed
            // — the critical stream stays open for the connection lifetime.
            byte[] preamble = BuildControlStreamPreamble(_qpackOptions);
            await controlStream.Output.WriteAsync(preamble, cancellationToken).ConfigureAwait(false);

            // When the dynamic table is enabled the server also opens its own
            // QPACK decoder stream (RFC 9204 §4.2) so it can send decoder
            // instructions (Insert Count Increment, and — with a stream ID —
            // Section Acknowledgment). Like the control stream it is a critical
            // stream: its type prefix is written and it is left open.
            if (_decoderState is not null)
            {
                IMultiplexedConnectionStream decoderStream = await _connection
                    .OpenStreamAsync(ConnectionDirection.WriteOnly, cancellationToken)
                    .ConfigureAwait(false);
                _decoderStream = decoderStream;

                byte[] decoderPrefix = BuildStreamTypePrefix(Http3StreamType.QPackDecoder);
                await decoderStream.Output.WriteAsync(decoderPrefix, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (QuicException)
        {
        }
        catch (ConnectionException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
        catch (Exception ex) when (IsWireLevelFailure(ex))
        {
        }
    }

    private static byte[] BuildStreamTypePrefix(long streamType)
    {
        using MemoryStream buffer = new();
        QuicVariableLengthInteger.Write(buffer, streamType);
        return buffer.ToArray();
    }

    /// <summary>
    /// Builds the bytes for the server control stream's opening: the RFC 9114
    /// §6.2 unidirectional stream-type prefix (0x00 = control) followed by a
    /// SETTINGS frame (type 0x04) carrying the server's advertised settings.
    /// </summary>
    private static byte[] BuildControlStreamPreamble(Http3QPackOptions qpackOptions)
    {
        using MemoryStream buffer = new();

        // RFC 9114 §6.2 — the control stream is identified by a stream-type
        // varint of 0x00 as its first bytes.
        QuicVariableLengthInteger.Write(buffer, Http3StreamType.Control);

        // RFC 9114 §6.2.1 / §7.2.4 — the first frame MUST be SETTINGS.
        byte[] settings = Http3LocalSettings.EncodePayload(qpackOptions);
        QuicVariableLengthInteger.Write(buffer, (long)Http3FrameType.Settings);
        QuicVariableLengthInteger.Write(buffer, settings.Length);
        buffer.Write(settings, 0, settings.Length);

        return buffer.ToArray();
    }

    /// <summary>
    /// Ends the receive side when the enumeration completes: signals teardown (stopping the accept loop
    /// and any head read still in flight), waits for the accept loop to finish, and rejects the requests
    /// that were assembled but never handed to the consumer.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Exchanges already handed out keep running — the server may dispatch each on its own task — so
    /// nothing they still need is torn down here: request-body reads, trailer decodes, the decoder-stream
    /// gate, and the background control-stream and QPACK stream drains all live until the connection
    /// itself closes (<see cref="IMultiplexedConnection.ConnectionClosed"/>). The drains end on their
    /// own then. None of them throws: a violation one finds closes the connection with its error (#1083),
    /// and a read that fails because the connection is ending reports nothing, so nothing awaits them.
    /// </para>
    /// <para>
    /// The outbound control stream is deliberately not completed here — teardown stays
    /// connection-first, so the multiplexed connection's own disposal (via
    /// <see cref="Http3Connection.DisposeAsync"/>) closes the QUIC connection before releasing its
    /// streams, and a peer never observes <c>H3_CLOSED_CRITICAL_STREAM</c> ahead of
    /// <c>CONNECTION_CLOSE</c>.
    /// </para>
    /// </remarks>
    private async Task ShutdownAsync()
    {
        // The enumeration has ended, so there is nothing left for an idle close to end.
        StopKeepAlive();

        if (!_teardownSource.IsCancellationRequested)
        {
            _teardownSource.Cancel();
        }

        if (_acceptLoopTask is not null)
        {
            // The loop never faults — it forwards any failure through the channel — and it completes the
            // channel only after every stream it accepted has finished its head processing.
            await _acceptLoopTask.ConfigureAwait(false);
        }

        // Requests assembled but never handed to the consumer (it stopped enumerating first) will never
        // be processed. RFC 9114 §4.1.1 — reject them with H3_REQUEST_REJECTED so the peer may retry.
        while (_readyContexts.Reader.TryRead(out Http3Context? undispatched))
        {
            await RejectUndispatchedAsync(undispatched).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Gets the token signalled when the QUIC connection itself closes or aborts. Request-body reads
    /// observe it so a body read outliving the receive enumeration still ends — with a clean stream
    /// error — when the connection goes away.
    /// </summary>
    internal CancellationToken ConnectionClosed => _connection.ConnectionClosed;

    // Intake teardown also follows remote closure; only an actual locally initiated connection close
    // excludes peer-fault classification. A graceful GOAWAY still lets existing responses run.
    internal void MarkLocalConnectionClose() => _localConnectionClose = true;

    /// <summary>
    /// Emits the server's <c>GOAWAY</c> frame on the outbound control stream
    /// (RFC 9114 §5.2 / §7.2.6) to announce graceful shutdown before the QUIC
    /// connection is closed. The announced identifier is the lowest
    /// client-initiated bidirectional stream ID the server will not process —
    /// derived from the count of request streams already accepted using QUIC's
    /// client-bidi numbering (ID = 4 × <c>n</c>) — so requests at or below the
    /// highest accepted stream may finish while later streams are rejected.
    /// </summary>
    /// <param name="cancellationToken">Cancels the control-stream write.</param>
    /// <returns>A task that completes once the GOAWAY has been written (or skipped).</returns>
    /// <remarks>
    /// <para>
    /// One-shot: repeated calls after the first write are no-ops, so the announced
    /// boundary never grows (RFC 9114 §5.2). When the receive loop has not run (no
    /// control stream was opened) there is nothing to announce yet: the call
    /// returns without writing and leaves the one-shot to a later call.
    /// </para>
    /// <para>
    /// Best-effort, like the SETTINGS emission: writing GOAWAY requires a live
    /// control stream, so a wire-level or QUIC failure here is swallowed — the
    /// connection is tearing down regardless, and the QUIC <c>CONNECTION_CLOSE</c>
    /// that follows conveys the shutdown even if the frame did not land.
    /// </para>
    /// </remarks>
    internal async Task SendGoAwayAsync(CancellationToken cancellationToken = default)
    {
        IMultiplexedConnectionStream? controlStream = Volatile.Read(ref _controlStream);
        if (controlStream is null)
        {
            // The server never opened its control stream (the receive loop did
            // not run), so it advertised no SETTINGS and has no critical stream
            // to carry GOAWAY. The QUIC close alone tears the connection down.
            return;
        }

        if (Interlocked.Exchange(ref _goAwaySent, 1) == 1)
        {
            return;
        }

        try
        {
            // RFC 9114 §5.2 — streams with an ID below the announced value may
            // have been processed. The lowest unprocessed client-initiated
            // bidirectional stream is (accepted count) × 4.
            long goAwayStreamId = (long)Volatile.Read(ref _processedRequestStreamCount) * 4L;
            byte[] frame = Http3GoAwayFrame.Encode(goAwayStreamId);
            await controlStream.Output.WriteAsync(frame, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        catch (QuicException)
        {
        }
        catch (ConnectionException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
        catch (Exception ex) when (IsWireLevelFailure(ex))
        {
        }
    }

    /// <inheritdoc />
    /// <remarks>
    /// <para>
    /// RFC 9114 §5.2. Signals teardown, which stops the accept loop: no request stream is accepted from
    /// here on, and a request whose head was still arriving is reset with <c>H3_REQUEST_REJECTED</c>
    /// (the peer may retry it). Once the accept loop has stopped, the <c>GOAWAY</c> is written in the
    /// background, carrying the first stream the connection did not accept — the same boundary the
    /// teardown GOAWAY carries, so exactly one is ever sent. The receive enumeration ends after the
    /// requests already published; the exchanges it yielded keep their request bodies and response
    /// streams until they finish.
    /// </para>
    /// <para>
    /// A request stream the peer opens after this point is at or above that boundary, so it is never
    /// dispatched: until the connection closes, each one is accepted and reset with
    /// <c>H3_REQUEST_REJECTED</c> at once, unread, and the peer may retry it on another connection
    /// (RFC 9114 §5.2, #1306).
    /// </para>
    /// </remarks>
    public override void BeginGracefulClose()
    {
        if (Interlocked.Exchange(ref _gracefulCloseStarted, 1) == 1)
        {
            return;
        }

        _teardownSource.Cancel();
        Volatile.Write(ref _gracefulCloseAnnouncement, AnnounceGracefulCloseAsync());
    }

    /// <summary>
    /// Finishes a graceful close as the connection is disposed: waits for the GOAWAY a host's
    /// <see cref="BeginGracefulClose"/> started, then writes the GOAWAY if none has been written yet.
    /// Never faults.
    /// </summary>
    internal async Task CompleteGracefulCloseAsync()
    {
        if (Volatile.Read(ref _acceptLoopTask) is null)
        {
            // The receive enumeration never started, and nothing starts it during disposal, so no stream
            // will be accepted: the announcement must not wait for an accept loop that will never run.
            _acceptStopped.TrySetResult();
        }

        if (Volatile.Read(ref _gracefulCloseAnnouncement) is { } announcement)
        {
            await announcement.ConfigureAwait(false);
        }

        await SendGoAwayAsync().ConfigureAwait(false);
    }

    private async Task AnnounceGracefulCloseAsync()
    {
        // Off the caller's thread: a host begins the graceful close of all its connections from one stop
        // signal, and no connection's write may hold up the others.
        await Task.CompletedTask.ConfigureAwait(ConfigureAwaitOptions.ForceYielding);

        // The accept loop observes the teardown signal; once it has stopped, the accepted-stream count
        // the GOAWAY boundary is derived from is final.
        await _acceptStopped.Task.ConfigureAwait(false);
        await SendGoAwayAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// Starts the keep-alive deadline when the receive loop starts (#1085): the connection is idle until
    /// its first request stream arrives. Nothing is armed for an infinite
    /// <see cref="HttpConnectionListenerLimits.KeepAliveTimeout"/>.
    /// </summary>
    private void StartKeepAlive()
    {
        TimeSpan keepAliveTimeout = _limits.KeepAliveTimeout;
        if (keepAliveTimeout == Timeout.InfiniteTimeSpan)
        {
            return;
        }

        lock (_keepAliveLock)
        {
            if (_keepAliveTimer is not null || _keepAliveStopped)
            {
                return;
            }

            // The deadline is read once, as HTTP/2 reads it when its frame pump starts.
            _keepAliveTimeout = keepAliveTimeout;
            _idleSince = _timeProvider.GetTimestamp();
            _keepAliveTimer = _timeProvider.CreateTimer(
                static state => ((Http3ConnectionContext)state!).OnKeepAliveTimer(),
                this,
                _activeRequestStreams == 0 ? _keepAliveTimeout : Timeout.InfiniteTimeSpan,
                Timeout.InfiniteTimeSpan);
        }
    }

    /// <summary>
    /// Stops the keep-alive deadline for good: the receive enumeration has ended, or an idle close began.
    /// </summary>
    private void StopKeepAlive()
    {
        ITimer? timer;

        lock (_keepAliveLock)
        {
            _keepAliveStopped = true;
            timer = _keepAliveTimer;
            _keepAliveTimer = null;
        }

        timer?.Dispose();
    }

    /// <summary>
    /// Counts an accepted request stream as in flight, so the connection is not idle (#1085).
    /// </summary>
    private void BeginRequestStream()
    {
        lock (_keepAliveLock)
        {
            if (_activeRequestStreams++ == 0)
            {
                _keepAliveTimer?.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            }
        }
    }

    /// <summary>
    /// Ends a request stream's time in flight (#1085): its head yielded no exchange, or its exchange
    /// ended. The keep-alive deadline runs from the later of the connection's start and the end of its
    /// last exchange, so only an exchange's end moves it: a stream that never became one — reset for a
    /// head that timed out or was malformed, answered <c>431</c>, or rejected by a request-parse
    /// interceptor — cannot keep an idle connection open. The last stream to end arms the deadline for
    /// what is left of it, at once when nothing is.
    /// </summary>
    /// <param name="exchangeEnded">Whether the stream's exchange ended, rather than its head yielding none.</param>
    private void EndRequestStream(bool exchangeEnded)
    {
        lock (_keepAliveLock)
        {
            if (exchangeEnded)
            {
                _idleSince = _timeProvider.GetTimestamp();
            }

            if (--_activeRequestStreams == 0 && _keepAliveTimer is not null && !_keepAliveStopped)
            {
                TimeSpan remaining = _keepAliveTimeout - _timeProvider.GetElapsedTime(_idleSince);
                _keepAliveTimer.Change(remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero, Timeout.InfiniteTimeSpan);
            }
        }
    }

    /// <summary>
    /// Ends <paramref name="context"/>'s exchange on this connection: its <c>SendAsync</c> returned or
    /// threw, or it was disposed. A <c>SendAsync</c> that refused the head or a buffered trailer section
    /// (#1183) does not call this: nothing reached the wire and the response has not started, so the
    /// exchange stays running, and its request stream keeps the connection busy, until the caller
    /// finalizes it again or disposes it. Idempotent per exchange.
    /// </summary>
    /// <param name="context">The exchange that ended.</param>
    internal void EndExchange(Http3Context context)
    {
        if (context.TryEndExchange())
        {
            CloseRequestStreamPriority(context.StreamId);
            EndRequestStream(exchangeEnded: true);
        }
    }

    /// <summary>
    /// Closes the connection once it has been idle for <see cref="HttpConnectionListenerLimits.KeepAliveTimeout"/>
    /// (#1085). RFC 9114 §5.2 — the server closes gracefully: <see cref="BeginGracefulClose"/> stops
    /// accepting, writes the <c>GOAWAY</c> that tells the peer no request stream beyond it was processed,
    /// and ends the receive enumeration, after which the host disposes the connection, which closes the
    /// QUIC connection with <c>H3_NO_ERROR</c>. A timer that fires after the connection became busy, or
    /// that a later idle period re-armed, changes nothing. Unlike QUIC's own idle timeout, which any
    /// packet resets, this deadline is not moved by PING or other connection-level traffic.
    /// </summary>
    private void OnKeepAliveTimer()
    {
        lock (_keepAliveLock)
        {
            if (_keepAliveStopped || _keepAliveTimer is null || _activeRequestStreams > 0)
            {
                return;
            }

            TimeSpan remaining = _keepAliveTimeout - _timeProvider.GetElapsedTime(_idleSince);
            if (remaining > TimeSpan.Zero)
            {
                _keepAliveTimer.Change(remaining, Timeout.InfiniteTimeSpan);
                return;
            }

            _keepAliveStopped = true;
        }

        BeginGracefulClose();
    }

    /// <summary>
    /// Raises a connection error (RFC 9114 §8): records it (the first one wins), signals teardown so the
    /// accept loop and every head read in flight stop, and aborts the multiplexed connection with the
    /// error as the reason — which signals <see cref="ConnectionClosed"/>, ending the drains, any blocked
    /// QPACK decode, and every request-body read still in flight. The enumeration ends after the contexts
    /// already published. Called from any stream's processing and from request-body reads; idempotent.
    /// </summary>
    /// <remarks>
    /// The connection closes with the error's RFC 9114 §8.1 / RFC 9204 §6 code wherever the driver can
    /// carry one. The multiplexed root delegates code validation and carriage to the driver.
    /// </remarks>
    /// <param name="error">The connection error, carrying the code the connection is closed with.</param>
    internal void AbortConnection(Http3ConnectionException error)
    {
        if (Interlocked.CompareExchange(ref _connectionError, error, null) is not null)
        {
            return;
        }

        HttpTransportEventSource.Log.ConnectionError(_connection.Id, "3", (long)error.ErrorCode);
        MarkLocalConnectionClose();
        _teardownSource.Cancel();

        _connection.Abort((long)error.ErrorCode, error);
    }

    /// <summary>
    /// Resets one request stream with a stream error (RFC 9114 §8) — both directions, carrying
    /// <paramref name="reason"/> — leaving the connection and its other streams intact. When the dynamic
    /// table is enabled and the stream is abandoned before its end, a Stream Cancellation is emitted too
    /// (RFC 9204 §4.4.2): a field section the server never decoded (an unread trailer section, an
    /// oversized HEADERS frame) will never be acknowledged, and the peer encoder must release its
    /// references.
    /// </summary>
    /// <param name="streamConnection">The request stream.</param>
    /// <param name="requestStreamId">The request stream's wire ID.</param>
    /// <param name="resetState">The reset claim shared by every path that can end this request.</param>
    /// <param name="reason">The stream error, carrying the RFC 9114 §8.1 code.</param>
    /// <param name="abandonsReading">Whether the stream still had unread octets.</param>
    internal void ResetRequestStream(IMultiplexedConnectionStream streamConnection, long requestStreamId, Http3RequestResetState resetState, Http3StreamException reason, bool abandonsReading)
    {
        if (ResetStream(streamConnection, reason, _connection.Id, requestStreamId, resetState)
            && abandonsReading && _decoderState is not null)
        {
            _ = TrySendStreamCancellationAsync(requestStreamId);
        }
    }

    /// <summary>
    /// Resets a request stream in both directions with the stream error's RFC 9114 §8.1 code
    /// (<c>RESET_STREAM</c> and <c>STOP_SENDING</c>), then aborts it with the error as the reason, which
    /// ends the stream's lifecycle and fires the exchange's <c>RequestCancelled</c>. The code reaches the
    /// wire wherever the stream transport can carry one. The root operation ends both directions and
    /// the lifecycle together, preserving an already-ended direction's first code.
    /// </summary>
    /// <param name="streamConnection">The request stream.</param>
    /// <param name="reason">The stream error, carrying the code.</param>
    /// <param name="connectionId">The parent multiplexed connection's identifier.</param>
    /// <param name="streamId">The request stream's wire ID.</param>
    /// <param name="resetState">The first-wins reset claim for this request, including before dispatch.</param>
    /// <returns>Whether this call owns reset cleanup, even if the peer already ended the stream.</returns>
    internal static bool ResetStream(IMultiplexedConnectionStream streamConnection, Http3StreamException reason, ConnectionId connectionId, long streamId, Http3RequestResetState resetState)
    {
        // A body failure and response timeout can both observe an open driver. Claim before reporting:
        // a Meter listener runs inline, so even its callbacks must not permit another reset decision.
        if (!resetState.TryClaimReset())
        {
            return false;
        }

        if (streamConnection.State is not (ConnectionState.Aborted or ConnectionState.Closed))
        {
            HttpTransportEventSource.Log.StreamReset(connectionId, "3", streamId, (long)reason.ErrorCode);
            streamConnection.Abort((long)reason.ErrorCode, reason);
        }

        // The peer may already have reset this stream, but abandoned field sections still require
        // the caller's QPACK cancellation. Only the winning cleanup path emits that instruction.
        return true;
    }

    /// <summary>Reports the transport's first decision to reject one request.</summary>
    internal void ReportRequestRejected(long streamId, HttpStatusCode statusCode)
    {
        HttpTransportEventSource.Log.RequestRejected(_connection.Id, "3", streamId, statusCode.Value);
    }

    /// <summary>
    /// Stops reading a request stream with <paramref name="errorCode"/> (<c>STOP_SENDING</c>), leaving the
    /// response direction and the stream's lifecycle alone. Has no effect on a stream that cannot carry a
    /// code, whose driver stops it with its default code when its input is completed.
    /// </summary>
    /// <param name="streamConnection">The request stream.</param>
    /// <param name="errorCode">The RFC 9114 §8.1 code the peer receives.</param>
    internal static void StopReadingWithCode(IMultiplexedConnectionStream streamConnection, Http3ErrorCode errorCode)
    {
        if (streamConnection.DirectionalAbort is { } coded)
        {
            coded.AbortRead((long)errorCode);
        }
    }

    /// <summary>
    /// Processes one peer-initiated unidirectional stream on its own, off the accept loop, and turns a
    /// connection-level violation into a connection error.
    /// </summary>
    private async Task ProcessUnidirectionalStreamAsync(IMultiplexedConnectionStream streamConnection, CancellationToken receiveToken)
    {
        try
        {
            using CancellationTokenSource streamCancellation =
                CancellationTokenSource.CreateLinkedTokenSource(receiveToken, _teardownSource.Token);

            await HandleUnidirectionalStreamAsync(streamConnection, streamCancellation.Token).ConfigureAwait(false);
        }
        finally
        {
            EndStreamWork();
        }
    }

    /// <summary>
    /// Types and handles one peer-initiated unidirectional stream, turning a connection-level violation into
    /// a connection error. <paramref name="cancellationToken"/> bounds only the stream's opening — its type
    /// and, for a control stream, its SETTINGS frame; the drains that follow live as long as the connection.
    /// </summary>
    private async Task HandleUnidirectionalStreamAsync(IMultiplexedConnectionStream streamConnection, CancellationToken cancellationToken)
    {
        try
        {
            Http3ConnectionException? violation = await TryHandleUnidirectionalStreamAsync(
                streamConnection,
                cancellationToken).ConfigureAwait(false);

            if (violation is not null)
            {
                AbortConnection(violation);
            }
        }
        catch (OperationCanceledException)
        {
            // Teardown before the stream's opening octets arrived — nothing to process.
        }
        // Not swallowed: an unexpected fault (a programmer error) is forwarded to the consumer through
        // the channel, and the enumeration rethrows it (once the enumeration has ended, nothing can).
        catch (Exception exception)
        {
            _readyContexts.Writer.TryComplete(exception);
        }
    }

    /// <summary>
    /// Demultiplexes a peer-initiated unidirectional stream by its RFC 9114
    /// §6.2 stream-type prefix. Returns the connection error when the stream is
    /// a connection-level protocol violation — a duplicate control or QPACK
    /// stream (<c>H3_STREAM_CREATION_ERROR</c>), a missing or non-SETTINGS first
    /// control frame (<c>H3_MISSING_SETTINGS</c>), a malformed or oversized
    /// SETTINGS frame (<c>H3_FRAME_ERROR</c>), or a client-created push stream
    /// (<c>H3_STREAM_CREATION_ERROR</c>); otherwise <see langword="null"/>.
    /// </summary>
    private async Task<Http3ConnectionException?> TryHandleUnidirectionalStreamAsync(
        IMultiplexedConnectionStream streamConnection,
        CancellationToken cancellationToken)
    {
        // RFC 9114 §6.2 — read directly off the stream connection's PipeReader
        // rather than the Stream adapter. Unidirectional streams are processed
        // incrementally (a varint stream-type prefix, then type-specific
        // frames), which the buffered ReadOnlySequence model expresses directly
        // without the adapter's read-size quirks.
        PipeReader reader = streamConnection.Input;

        long? streamType;
        try
        {
            streamType = await ReadVarintAsync(reader, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (IsPerStreamFailure(ex))
        {
            // The stream type could not be read — RFC 9114 §6.2 permits
            // abandoning an unparseable unidirectional stream without
            // affecting the connection.
            await AbandonUnidirectionalStreamAsync(streamConnection).ConfigureAwait(false);
            return null;
        }

        if (streamType is null)
        {
            // The stream ended before its type: nothing to process, so it is released.
            await AbandonUnidirectionalStreamAsync(streamConnection).ConfigureAwait(false);
            return null;
        }

        switch (streamType.Value)
        {
            case Http3StreamType.Control:
                return await TryHandleControlStreamAsync(reader, cancellationToken).ConfigureAwait(false);

            case Http3StreamType.QPackEncoder:
                // RFC 9204 §4.2 — at most one encoder stream. With the table
                // enabled, drain it in the background so its Set Capacity /
                // Insert / Duplicate instructions populate the dynamic table while
                // requests keep being served. With the dynamic table disabled
                // (QPACK_MAX_TABLE_CAPACITY = 0), validate the zero-capacity profile
                // without buffering instructions or constructing a dynamic table.
                if (Interlocked.Exchange(ref _qpackEncoderStreamReceived, 1) == 1)
                {
                    return new Http3ConnectionException(
                        Http3ErrorCode.StreamCreationError,
                        ErrorMessages.PeerOpenedASecondQpackEncoderStreamRfc920442);
                }

                // Connection-lived: a request body read after the enumeration ends may still wait on
                // insertions the drain applies. Neither loop faults, so neither is awaited.
                _ = _decoderState is not null
                    ? DrainQPackEncoderStreamAsync(reader)
                    : DrainDisabledQPackEncoderStreamAsync(reader);

                return null;

            case Http3StreamType.QPackDecoder:
                if (Interlocked.Exchange(ref _qpackDecoderStreamReceived, 1) == 1)
                {
                    return new Http3ConnectionException(
                        Http3ErrorCode.StreamCreationError,
                        ErrorMessages.PeerOpenedASecondQpackDecoderStreamRfc920442);
                }

                // The server's encoder never references the dynamic table, so the peer's decoder stream may
                // carry only Stream Cancellations, which need nothing; it is read for the connection's
                // lifetime, so its other instructions and its closure are seen (RFC 9204 §4.2, §4.4).
                _ = WatchQPackDecoderStreamAsync(reader);
                return null;

            case Http3StreamType.Push:
                // RFC 9114 §6.2.2 — a client MUST NOT create a push stream;
                // treat it as H3_STREAM_CREATION_ERROR.
                return new Http3ConnectionException(
                    Http3ErrorCode.StreamCreationError,
                    ErrorMessages.ClientOpenedAPushStreamRfc9114622);

            default:
                // RFC 9114 §6.2 — an unknown or reserved stream type is not an
                // error, but its recipient MUST abort reading it or discard its
                // data, and SHOULD abort with H3_STREAM_CREATION_ERROR.
                await AbandonUnidirectionalStreamAsync(streamConnection).ConfigureAwait(false);
                return null;
        }
    }

    /// <summary>
    /// Abandons a peer-initiated unidirectional stream the server will not read — an unknown or reserved
    /// stream type, or one whose type could not be read (RFC 9114 §6.2): reading is aborted with
    /// <c>STOP_SENDING(H3_STREAM_CREATION_ERROR)</c>, the code the RFC recommends, and the stream is
    /// released, so it no longer holds one of the peer's unidirectional-stream credits or the data in its
    /// flow-control window until the connection closes. On a stream that cannot carry a code the release
    /// alone stops it, with the driver's default code. Best-effort: a stream already gone has nothing left
    /// to release.
    /// </summary>
    /// <param name="streamConnection">The unidirectional stream.</param>
    /// <returns>A task that completes once the stream is released.</returns>
    private static async Task AbandonUnidirectionalStreamAsync(IMultiplexedConnectionStream streamConnection)
    {
        StopReadingWithCode(streamConnection, Http3ErrorCode.StreamCreationError);

        try
        {
            await streamConnection.DisposeAsync().ConfigureAwait(false);
        }
        catch (InvalidOperationException)
        {
            // ObjectDisposedException included: the stream was released with its connection.
        }
        catch (ConnectionException)
        {
        }
        catch (Exception exception) when (IsWireLevelFailure(exception))
        {
        }
    }

    /// <summary>
    /// Reads and applies the peer's control stream. Enforces a single control
    /// stream and that its first frame is SETTINGS (RFC 9114 §6.2.1 / §7.2.4),
    /// then hands the stream to a background drain for the connection lifetime.
    /// Returns the connection error on a protocol violation.
    /// </summary>
    private async Task<Http3ConnectionException?> TryHandleControlStreamAsync(
        PipeReader reader,
        CancellationToken cancellationToken)
    {
        if (Interlocked.Exchange(ref _controlStreamReceived, 1) == 1)
        {
            // RFC 9114 §6.2.1 — only one control stream per peer.
            return new Http3ConnectionException(
                Http3ErrorCode.StreamCreationError,
                ErrorMessages.PeerOpenedASecondControlStreamRfc9114621);
        }

        try
        {
            // RFC 9114 §6.2.1 / §7.2.4 — the first frame on the control stream
            // MUST be SETTINGS. Read and apply it before draining the rest so a
            // missing or non-SETTINGS opening frame terminates the connection.
            long? frameType = await ReadVarintAsync(reader, cancellationToken).ConfigureAwait(false);
            long? frameLength = frameType is null
                ? null
                : await ReadVarintAsync(reader, cancellationToken).ConfigureAwait(false);

            if (frameType is null || frameLength is null || frameType.Value != (long)Http3FrameType.Settings)
            {
                return new Http3ConnectionException(
                    Http3ErrorCode.MissingSettings,
                    ErrorMessages.PeerSControlStreamDidNotOpenWithASettingsFrameRfc);
            }

            if (frameLength.Value > maxControlFramePayloadSize)
            {
                return new Http3ConnectionException(
                    Http3ErrorCode.FrameError,
                    string.Format(CultureInfo.CurrentCulture, ErrorMessages.PeerSSettingsFrameDeclaresOctetsBeyondTheOctetBound, frameLength.Value, maxControlFramePayloadSize));
            }

            byte[] payload = await ReadExactAsync(reader, (int)frameLength.Value, cancellationToken).ConfigureAwait(false);
            ApplySettings(payload);
        }
        catch (Exception ex) when (IsStreamReadFailure(ex) && IsCriticalStreamResetByPeer(ex))
        {
            // RFC 9114 §6.2.1 — the control stream is critical from its first octet.
            return new Http3ConnectionException(
                Http3ErrorCode.ClosedCriticalStream,
                ErrorMessages.PeerResetItsControlStreamBeforeItsSettingsFrameWasCompleteRfc,
                ex);
        }
        catch (Exception ex) when (IsPerStreamFailure(ex))
        {
            // The control stream is critical; a read/parse failure on the
            // mandatory SETTINGS frame is a connection error (RFC 9114 §7.1).
            return new Http3ConnectionException(
                Http3ErrorCode.FrameError,
                ErrorMessages.PeerSSettingsFrameWasTruncatedOrMalformedRfc911471,
                ex);
        }

        // RFC 9114 §6.2.1 — the control stream stays open for the connection
        // lifetime. Read its post-SETTINGS frames on a background task so they
        // cannot accumulate unread in the pipe, and so its closure is seen.
        // Connection-lived, and it never faults, so it is not awaited.
        _ = DrainPeerControlStreamAsync(reader);
        return null;
    }

    /// <summary>
    /// Reads the peer's control stream after its SETTINGS frame for the connection's lifetime —
    /// not the receive enumeration's — and closes the connection on any violation of the control
    /// stream's rules (RFC 9114 §6.2.1, §7.2; RFC 9218 §7.2). Never throws.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The control stream is critical, so the drain never stops quietly while the connection is
    /// open: a frame it would leave unread could be one that closes the connection. It ends when
    /// the connection closes, or with a connection error:
    /// </para>
    /// <list type="bullet">
    /// <item><description>The peer ends the stream (<c>FIN</c>) or resets it:
    /// <c>H3_CLOSED_CRITICAL_STREAM</c> (RFC 9114 §6.2.1).</description></item>
    /// <item><description>The stream ends inside a frame: <c>H3_FRAME_ERROR</c> (RFC 9114 §7.1).</description></item>
    /// <item><description>A frame that may not appear on a control stream — <c>DATA</c>, <c>HEADERS</c>,
    /// <c>PUSH_PROMISE</c>, a second <c>SETTINGS</c>, or a reserved HTTP/2 type: <c>H3_FRAME_UNEXPECTED</c>
    /// (RFC 9114 §7.2).</description></item>
    /// <item><description>A push <c>PRIORITY_UPDATE</c> or a <c>CANCEL_PUSH</c>: the server promises no
    /// pushes, so every push ID is one it never sent: <c>H3_ID_ERROR</c> (RFC 9218 §7.2, RFC 9114
    /// §7.2.3).</description></item>
    /// <item><description>A request <c>PRIORITY_UPDATE</c> whose Prioritized Element ID is not a
    /// client-initiated bidirectional stream: <c>H3_ID_ERROR</c> (RFC 9218 §7.2); one whose element ID
    /// is truncated: <c>H3_FRAME_ERROR</c>.</description></item>
    /// <item><description>A <c>GOAWAY</c> or <c>MAX_PUSH_ID</c> whose payload is not exactly one
    /// variable-length integer: <c>H3_FRAME_ERROR</c> (RFC 9114 §7.1).</description></item>
    /// <item><description>A <c>GOAWAY</c> whose identifier is larger than an earlier one's, or a
    /// <c>MAX_PUSH_ID</c> whose maximum is smaller than an earlier one's: <c>H3_ID_ERROR</c> (RFC 9114 §5.2,
    /// §7.2.7).</description></item>
    /// </list>
    /// <para>
    /// A request <c>PRIORITY_UPDATE</c> records the referenced stream's effective priority, within the
    /// priority state's bound (<see cref="RecordRequestStreamPriority"/>); one whose Priority Field Value
    /// cannot be parsed, or whose payload exceeds the 16 KiB this drain buffers, has no effect. A valid
    /// <c>GOAWAY</c> (§7.2.6) or <c>MAX_PUSH_ID</c> (§7.2.7) is otherwise not acted on: the server plays no
    /// client role in a graceful close and never pushes. Frames of unknown or reserved type (§9) are read and
    /// discarded.
    /// </para>
    /// </remarks>
    private async Task DrainPeerControlStreamAsync(PipeReader reader)
    {
        Http3ConnectionException? violation;

        try
        {
            violation = await ReadPeerControlFramesAsync(reader, _connection.ConnectionClosed).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // The connection closed — nothing more to read.
            return;
        }
        catch (EndOfStreamException exception)
        {
            violation = new Http3ConnectionException(
                Http3ErrorCode.FrameError,
                ErrorMessages.PeerSControlStreamEndedInsideAFrameRfc911471,
                exception);
        }
        catch (Exception exception) when (IsStreamReadFailure(exception))
        {
            violation = IsCriticalStreamResetByPeer(exception)
                ? new Http3ConnectionException(
                    Http3ErrorCode.ClosedCriticalStream,
                    ErrorMessages.PeerResetItsControlStreamRfc9114621,
                    exception)
                : null;
        }

        if (violation is not null)
        {
            AbortConnection(violation);
        }
    }

    /// <summary>
    /// Reads post-SETTINGS control frames until one ends the connection (see
    /// <see cref="DrainPeerControlStreamAsync"/>), and returns that connection error.
    /// </summary>
    private async Task<Http3ConnectionException> ReadPeerControlFramesAsync(PipeReader reader, CancellationToken cancellationToken)
    {
        // The last identifier each of the peer's GOAWAY and MAX_PUSH_ID frames carried, which the next one is
        // checked against (RFC 9114 §5.2, §7.2.7); null until the first.
        long? lastGoAwayId = null;
        long? lastMaxPushId = null;

        while (true)
        {
            long? frameType = await ReadVarintAsync(reader, cancellationToken).ConfigureAwait(false);
            if (frameType is null)
            {
                return new Http3ConnectionException(
                    Http3ErrorCode.ClosedCriticalStream,
                    ErrorMessages.PeerClosedItsControlStreamRfc9114621);
            }

            long? frameLength = await ReadVarintAsync(reader, cancellationToken).ConfigureAwait(false);
            if (frameLength is null)
            {
                return new Http3ConnectionException(
                    Http3ErrorCode.FrameError,
                    ErrorMessages.PeerSControlStreamEndedInsideAFrameHeaderRfc91147);
            }

            switch (frameType.Value)
            {
                case (long)Http3FrameType.PriorityUpdateRequest:
                    if (await ApplyPriorityUpdateAsync(reader, frameLength.Value, cancellationToken).ConfigureAwait(false) is { } priorityViolation)
                    {
                        return priorityViolation;
                    }

                    continue;

                case (long)Http3FrameType.PriorityUpdatePush:
                    return new Http3ConnectionException(
                        Http3ErrorCode.IdError,
                        ErrorMessages.PeerSentAPriorityUpdateForAPushAndTheServerPromises);

                case (long)Http3FrameType.CancelPush:
                    return new Http3ConnectionException(
                        Http3ErrorCode.IdError,
                        ErrorMessages.PeerCancelledAPushTheServerNeverPromisedRfc911472);

                case (long)Http3FrameType.GoAway:
                case (long)Http3FrameType.MaxPushId:
                    if (await ReadSingleIdentifierAsync(reader, frameLength.Value, cancellationToken).ConfigureAwait(false) is not { } identifier)
                    {
                        return new Http3ConnectionException(
                            Http3ErrorCode.FrameError,
                            string.Format(CultureInfo.CurrentCulture, ErrorMessages.PeerSFrameOfType0xDoesNotHoldExactlyOneVariable, frameType.Value));
                    }

                    if (frameType.Value == (long)Http3FrameType.GoAway)
                    {
                        // RFC 9114 §5.2 — a later GOAWAY may not carry a larger identifier than an earlier one.
                        if (identifier > lastGoAwayId)
                        {
                            return new Http3ConnectionException(
                                Http3ErrorCode.IdError,
                                string.Format(CultureInfo.CurrentCulture, ErrorMessages.PeerSGoawayRaisedItsIdentifierFromToRfc911452, lastGoAwayId, identifier));
                        }

                        lastGoAwayId = identifier;
                    }
                    else
                    {
                        // RFC 9114 §7.2.7 — a client may not reduce the maximum push ID it advertised.
                        if (identifier < lastMaxPushId)
                        {
                            return new Http3ConnectionException(
                                Http3ErrorCode.IdError,
                                string.Format(CultureInfo.CurrentCulture, ErrorMessages.PeerSMaxPushIdLoweredTheMaximumPushIdFromTo, lastMaxPushId, identifier));
                        }

                        lastMaxPushId = identifier;
                    }

                    continue;

                case (long)Http3FrameType.Data:
                case (long)Http3FrameType.Headers:
                case (long)Http3FrameType.Settings:
                case (long)Http3FrameType.PushPromise:
                case (long)Http3FrameType.ReservedHttp2Priority:
                case (long)Http3FrameType.ReservedHttp2Ping:
                case (long)Http3FrameType.ReservedHttp2WindowUpdate:
                case (long)Http3FrameType.ReservedHttp2Continuation:
                    return new Http3ConnectionException(
                        Http3ErrorCode.UnexpectedFrame,
                        string.Format(CultureInfo.CurrentCulture, ErrorMessages.PeerSentAFrameOfType0xOnItsControlStreamWhere, frameType.Value));

                default:
                    // Frames of unknown or reserved type (RFC 9114 §9).
                    await SkipAsync(reader, frameLength.Value, cancellationToken).ConfigureAwait(false);
                    continue;
            }
        }
    }

    /// <summary>
    /// Reads the payload of a <c>GOAWAY</c> or <c>MAX_PUSH_ID</c> frame, whose only field is one variable-length
    /// integer (RFC 9114 §7.2.6, §7.2.7). Returns <see langword="null"/> when the payload holds anything else —
    /// nothing, a truncated integer, or octets after it — which RFC 9114 §7.1 makes <c>H3_FRAME_ERROR</c>.
    /// </summary>
    private static async Task<long?> ReadSingleIdentifierAsync(PipeReader reader, long length, CancellationToken cancellationToken)
    {
        // A variable-length integer takes at most eight octets, so a longer payload cannot be one; it is not read.
        if (length is 0 or > QuicVariableLengthInteger.MaxLength)
        {
            return null;
        }

        byte[] payload = await ReadExactAsync(reader, (int)length, cancellationToken).ConfigureAwait(false);

        // The two high bits of the first octet give the integer's length (RFC 9000 §16).
        if (payload.Length != 1 << (payload[0] >> 6))
        {
            return null;
        }

        int index = 0;
        return QuicVariableLengthInteger.Decode(payload, ref index);
    }

    /// <summary>
    /// Reads a request <c>PRIORITY_UPDATE</c> payload (RFC 9218 §7.2) and records the referenced
    /// stream's effective priority (<see cref="RecordRequestStreamPriority"/>, which bounds what it keeps).
    /// Returns the connection error when the Prioritized Element ID is
    /// truncated (<c>H3_FRAME_ERROR</c>), names no client-initiated bidirectional stream or exceeds
    /// the transport's published allowance (<c>H3_ID_ERROR</c>); otherwise <see langword="null"/>.
    /// A payload beyond the 16 KiB bound is skipped after validating its element ID, and one whose
    /// Priority Field Value does not parse is ignored: neither has effect.
    /// </summary>
    private async Task<Http3ConnectionException?> ApplyPriorityUpdateAsync(PipeReader reader, long length, CancellationToken cancellationToken)
    {
        bool oversized = length > maxControlFramePayloadSize;
        // Eight bytes cover every QUIC varint. Even an ignored field value must not hide an invalid ID.
        byte[] payload = await ReadExactAsync(reader, oversized ? QuicVariableLengthInteger.MaxLength : (int)length, cancellationToken).ConfigureAwait(false);

        if (!Http3PriorityUpdate.TryReadPrioritizedElementId(payload, out long prioritizedStreamId, out int consumed))
        {
            return new Http3ConnectionException(
                Http3ErrorCode.FrameError,
                ErrorMessages.PeerSPriorityUpdateEndedInsideItsPrioritizedElementIdRfc9114);
        }

        // RFC 9000 §2.1 — a client-initiated bidirectional stream ID is a multiple of 4.
        if ((prioritizedStreamId & 0x3) != 0)
        {
            return new Http3ConnectionException(
                Http3ErrorCode.IdError,
                string.Format(CultureInfo.CurrentCulture, ErrorMessages.PeerSPriorityUpdateNamesStreamWhichIsNotARequestStream, prioritizedStreamId));
        }

        // RFC 9218 §7.2 — IDs at or beyond four times the cumulative stream allowance are invalid.
        // Divide the nonnegative ID instead of multiplying the limit, which could overflow.
        if (_streamLimits is { } limits
            && prioritizedStreamId / 4 >= limits.InboundBidirectionalStreamLimit)
        {
            return new Http3ConnectionException(
                Http3ErrorCode.IdError,
                string.Format(CultureInfo.CurrentCulture, ErrorMessages.PeerSPriorityUpdateNamesStreamBeyondTheInboundStreamAllowanceRfc, prioritizedStreamId));
        }

        if (oversized)
        {
            await SkipAsync(reader, length - payload.Length, cancellationToken).ConfigureAwait(false);
            return null;
        }

        if (HttpPriorityFieldValue.TryParse(payload.AsSpan(consumed), out HttpPriority priority))
        {
            RecordRequestStreamPriority(prioritizedStreamId, priority);
        }

        return null;
    }

    /// <summary>
    /// Decodes the peer encoder stream with the dynamic table disabled (#1423). Only Set Capacity 0
    /// is legal; any other instruction closes with QPACK_ENCODER_STREAM_ERROR. The peer ending or
    /// resetting the stream is a connection error of type
    /// <c>H3_CLOSED_CRITICAL_STREAM</c> (RFC 9204 §4.2). Never throws.
    /// </summary>
    /// <param name="reader">The stream's input, positioned after its stream type.</param>
    private async Task DrainDisabledQPackEncoderStreamAsync(PipeReader reader)
    {
        CancellationToken cancellationToken = _connection.ConnectionClosed;
        Http3ConnectionException? violation;

        try
        {
            while (true)
            {
                ReadResult result = await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
                bool invalid = false;
                foreach (ReadOnlyMemory<byte> segment in result.Buffer)
                {
                    // RFC 9204 §4.3: 001xxxxx is Set Capacity. Only 00100000 fits the advertised
                    // maximum of zero; a continued integer is already >= 31. Every other prefix is
                    // Insert or Duplicate, neither of which can fit/reference the empty table.
                    // Reject from the prefix, without buffering attacker-controlled string lengths.
                    if (segment.Span.IndexOfAnyExcept((byte)0x20) >= 0)
                    {
                        invalid = true;
                        break;
                    }
                }

                reader.AdvanceTo(result.Buffer.End);

                if (invalid)
                {
                    violation = new Http3ConnectionException(
                        Http3ErrorCode.QPackEncoderStreamError,
                        ErrorMessages.PeerSentAQpackEncoderInstructionIncompatibleWithAMaximumTableCapacity);
                    break;
                }

                if (result.IsCompleted)
                {
                    violation = new Http3ConnectionException(
                        Http3ErrorCode.ClosedCriticalStream,
                        ErrorMessages.PeerClosedItsQpackEncoderStreamRfc920442);
                    break;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // The connection closed — nothing more to read.
            return;
        }
        catch (Exception exception) when (IsStreamReadFailure(exception))
        {
            violation = IsCriticalStreamResetByPeer(exception)
                ? new Http3ConnectionException(
                    Http3ErrorCode.ClosedCriticalStream,
                    ErrorMessages.PeerResetItsQpackEncoderStreamRfc920442,
                    exception)
                : null;
        }

        if (violation is not null)
        {
            AbortConnection(violation);
        }
    }

    /// <summary>
    /// Reads the peer's QPACK decoder stream for the connection's lifetime (RFC 9204 §4.4), and closes the
    /// connection on an instruction the server's encoder cannot have caused, or on the stream's closure.
    /// Never throws.
    /// </summary>
    /// <remarks>
    /// The server's encoder references only the static table and inserts nothing, so every field section it
    /// sends has a Required Insert Count of zero. A Stream Cancellation (§4.4.2) is therefore the one
    /// instruction a decoder may send it, and it needs nothing. A Section Acknowledgment (§4.4.1) refers to
    /// a stream with no unacknowledged section that has a non-zero Required Insert Count, and an Insert
    /// Count Increment (§4.4.3) would raise the Known Received Count beyond the zero entries the encoder
    /// sent; RFC 9204 makes each a connection error of type <c>QPACK_DECODER_STREAM_ERROR</c>, as is an
    /// integer that overflows. The peer ending or resetting the stream is <c>H3_CLOSED_CRITICAL_STREAM</c>
    /// (§4.2).
    /// </remarks>
    /// <param name="reader">The stream's input, positioned after its stream type.</param>
    private async Task WatchQPackDecoderStreamAsync(PipeReader reader)
    {
        CancellationToken cancellationToken = _connection.ConnectionClosed;
        Http3ConnectionException? violation;

        try
        {
            while (true)
            {
                ReadResult result = await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
                ReadOnlySequence<byte> buffer = result.Buffer;

                violation = SkipStreamCancellations(ref buffer);

                // Complete instructions are consumed; a trailing partial one waits for its next octets.
                reader.AdvanceTo(buffer.Start, result.Buffer.End);

                if (violation is not null)
                {
                    break;
                }

                if (result.IsCompleted)
                {
                    violation = new Http3ConnectionException(
                        Http3ErrorCode.ClosedCriticalStream,
                        ErrorMessages.PeerClosedItsQpackDecoderStreamRfc920442);
                    break;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // The connection closed — nothing more to read.
            return;
        }
        catch (Exception exception) when (IsStreamReadFailure(exception))
        {
            violation = IsCriticalStreamResetByPeer(exception)
                ? new Http3ConnectionException(
                    Http3ErrorCode.ClosedCriticalStream,
                    ErrorMessages.PeerResetItsQpackDecoderStreamRfc920442,
                    exception)
                : null;
        }

        if (violation is not null)
        {
            AbortConnection(violation);
        }
    }

    /// <summary>
    /// Consumes the complete Stream Cancellation instructions (RFC 9204 §4.4.2) at the start of
    /// <paramref name="buffer"/>, leaving it at the first octet not consumed. Returns the connection error when
    /// an instruction is any other (see <see cref="WatchQPackDecoderStreamAsync"/>) or its integer overflows;
    /// otherwise <see langword="null"/>, also when the buffer ends inside a Stream Cancellation.
    /// </summary>
    private static Http3ConnectionException? SkipStreamCancellations(ref ReadOnlySequence<byte> buffer)
    {
        // A 6-bit prefixed integer takes at most ten octets, so ten buffered octets decide each instruction.
        Span<byte> instruction = stackalloc byte[10];

        while (!buffer.IsEmpty)
        {
            byte first = buffer.FirstSpan[0];

            if ((first & 0x80) != 0)
            {
                return new Http3ConnectionException(
                    Http3ErrorCode.QPackDecoderStreamError,
                    ErrorMessages.PeerAcknowledgedAFieldSectionButTheServerSFieldSectionsReference);
            }

            if ((first & 0x40) == 0)
            {
                return new Http3ConnectionException(
                    Http3ErrorCode.QPackDecoderStreamError,
                    ErrorMessages.PeerSentAnInsertCountIncrementButTheServerSEncoderInserts);
            }

            int available = (int)Math.Min(buffer.Length, instruction.Length);
            buffer.Slice(0, available).CopyTo(instruction);
            int consumed = 0;

            try
            {
                if (!QPackPrefixedInteger.TryDecode(instruction[..available], ref consumed, prefixBits: 6, out _))
                {
                    return null;
                }
            }
            catch (InvalidDataException exception)
            {
                return new Http3ConnectionException(
                    Http3ErrorCode.QPackDecoderStreamError,
                    ErrorMessages.PeerSStreamCancellationCarriesAStreamIdThatOverflowsRfc9204,
                    exception);
            }

            buffer = buffer.Slice(consumed);
        }

        return null;
    }

    /// <summary>
    /// A read of a peer stream failing underneath its reader: a wire failure (<see cref="IOException"/>,
    /// which includes the QUIC driver's <see cref="QuicException"/>), the in-memory driver's peer reset
    /// (<see cref="ConnectionException"/>), or a stream released with its connection
    /// (<see cref="InvalidOperationException"/>, <see cref="ObjectDisposedException"/> included).
    /// </summary>
    private static bool IsStreamReadFailure(Exception exception)
    {
        return exception is IOException
            or ConnectionException
            or InvalidOperationException;
    }

    /// <summary>
    /// Determines whether a critical stream's read failed because the peer reset that stream while the
    /// connection stays open (RFC 9114 §6.2.1, RFC 9204 §4.2) — rather than because the connection is
    /// ending: this end closed or aborted it, the peer closed it, or it was lost. Only a peer reset is a
    /// <c>H3_CLOSED_CRITICAL_STREAM</c>; the other failures end the connection anyway.
    /// </summary>
    /// <param name="exception">The read's failure.</param>
    /// <returns><see langword="true"/> when the peer reset the stream on a connection that is still open.</returns>
    private bool IsCriticalStreamResetByPeer(Exception exception)
    {
        if (_connection.ConnectionClosed.IsCancellationRequested
            || _connection.State != ConnectionState.Open
            || Volatile.Read(ref _connectionError) is not null)
        {
            return false;
        }

        return exception switch
        {
            // RESET_STREAM; a connection close or loss carries another QuicError.
            QuicException quic => IsQuicStreamAborted(quic),
            // The in-memory driver's reset or abort of the peer's end.
            ConnectionException => true,
            // A stream released underneath, or a wire failure no driver attributes to the stream.
            _ => false,
        };
    }

    /// <summary>
    /// Whether a <see cref="QuicException"/> reports the peer aborting the stream (<c>RESET_STREAM</c> or
    /// <c>STOP_SENDING</c>). <see cref="QuicException.QuicError"/> exists only where System.Net.Quic does,
    /// which is everywhere a QUIC stream can fail: System.Net.Quic runs on Windows, Linux and macOS only, and
    /// the platform check is what lets the error be read on a build that targets every platform (CA1416).
    /// The one guard for every HTTP/3 read of the error: the critical-stream drains (#1083) and the request
    /// body's peer-abort check (#1378).
    /// </summary>
    internal static bool IsQuicStreamAborted(QuicException exception)
    {
        return (OperatingSystem.IsWindows() || OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
            && exception.QuicError == QuicError.StreamAborted;
    }

    /// <summary>
    /// Drains the peer's QPACK encoder stream for the connection lifetime,
    /// applying its Set Dynamic Table Capacity / Insert / Duplicate instructions
    /// (RFC 9204 §4.3) to the shared decoder dynamic table and emitting an Insert
    /// Count Increment (§4.4.3) on the server's decoder stream for each batch of
    /// applied insertions. A malformed instruction or table violation is a
    /// connection error (§2.2): it aborts the connection so the accept loop
    /// observes the failure and terminates. Runs only when the dynamic table is
    /// enabled, and for the connection's lifetime — not the receive
    /// enumeration's — because an exchange still reading its body after the
    /// enumeration ends may be waiting on insertions (a trailer section that
    /// references the dynamic table). It never throws.
    /// </summary>
    private async Task DrainQPackEncoderStreamAsync(PipeReader reader)
    {
        CancellationToken cancellationToken = _connection.ConnectionClosed;

        try
        {
            while (true)
            {
                ReadResult result = await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
                ReadOnlySequence<byte> buffer = result.Buffer;

                int consumed = 0;
                int insertions = 0;

                if (!buffer.IsEmpty)
                {
                    // Copy the unconsumed span so the parser can work over a
                    // contiguous buffer; instruction volumes are small.
                    byte[] bytes = buffer.ToArray();
                    consumed = _decoderState!.ApplyEncoderInstructions(bytes, out insertions);
                }

                if (insertions > 0)
                {
                    await SendDecoderInstructionAsync(
                        QPackDecoderInstructionEncoder.InsertCountIncrement(insertions),
                        cancellationToken).ConfigureAwait(false);
                }

                // Consume the complete instructions; keep the trailing partial
                // (examined = end) so the next read waits for more bytes.
                reader.AdvanceTo(buffer.GetPosition(consumed), buffer.End);

                if (result.IsCompleted)
                {
                    if (consumed < buffer.Length)
                    {
                        throw new QPackException(
                            Http3ErrorCode.QPackEncoderStreamError,
                            ErrorMessages.QpackEncoderStreamEndedInTheMiddleOfAnInstruction);
                    }

                    // RFC 9204 §4.2 — the encoder stream is critical: the peer must not close it.
                    AbortConnection(new Http3ConnectionException(
                        Http3ErrorCode.ClosedCriticalStream,
                        ErrorMessages.PeerClosedItsQpackEncoderStreamRfc920442));
                    break;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // The connection closed — stop draining.
        }
        catch (QPackException ex)
        {
            // A connection-level QPACK error (RFC 9204 §2.2 / §6): the abort closes the connection, which
            // releases any request stream blocked on pending insertions and ends the accept loop.
            AbortConnection(new Http3ConnectionException(ex.ErrorCode, ex.Message, ex));
        }
        catch (Exception ex) when (IsStreamReadFailure(ex))
        {
            // The peer reset its encoder stream, or stopped the server's decoder stream the drain writes
            // to (RFC 9204 §4.2) — or the connection is ending underneath the drain, which needs nothing
            // more.
            if (IsCriticalStreamResetByPeer(ex))
            {
                AbortConnection(new Http3ConnectionException(
                    Http3ErrorCode.ClosedCriticalStream,
                    ErrorMessages.PeerResetItsQpackEncoderStreamOrStoppedTheServerSDecoder,
                    ex));
            }
        }
        catch (Exception ex) when (IsPerStreamFailure(ex))
        {
            // A malformed instruction the parser did not report as a QPackException is still one.
            AbortConnection(new Http3ConnectionException(Http3ErrorCode.QPackEncoderStreamError, ErrorMessages.QpackEncoderStreamCarriedAMalformedInstruction, ex));
        }
    }

    /// <summary>
    /// Writes a QPACK decoder-stream instruction (Insert Count Increment, or —
    /// with a stream ID — Section Acknowledgment / Stream Cancellation) to the
    /// server's outbound decoder stream. The encoder-stream drain and the request
    /// streams all emit instructions here, so the write is serialized by
    /// <see cref="_decoderWriteGate"/> — a <see cref="PipeWriter"/> tolerates no
    /// concurrent writers.
    /// </summary>
    private async Task SendDecoderInstructionAsync(byte[] instruction, CancellationToken cancellationToken)
    {
        if (_decoderStream is null)
        {
            return;
        }

        await _decoderWriteGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _decoderStream.Output.WriteAsync(instruction, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _decoderWriteGate.Release();
        }
    }

    /// <summary>
    /// Emits a QPACK Stream Cancellation (RFC 9204 §4.4.2) for a request stream whose
    /// field sections will not all be acknowledged — a decode abandoned before completion, a
    /// stream reset, or reading abandoned before the stream's end — letting the peer encoder
    /// reclaim the outstanding references (RFC 9204 §2.2.2.2). Best-effort: the stream is being
    /// abandoned, often because connection teardown cancelled it, so a detached token is used to
    /// still attempt the write, and a decoder stream that is already gone is swallowed rather than
    /// surfaced.
    /// </summary>
    /// <param name="streamId">The abandoned request stream identifier.</param>
    private async Task TrySendStreamCancellationAsync(long streamId)
    {
        try
        {
            await SendDecoderInstructionAsync(
                QPackDecoderInstructionEncoder.StreamCancellation(streamId),
                CancellationToken.None).ConfigureAwait(false);
        }
        catch (InvalidOperationException)
        {
            // ObjectDisposedException included: the decoder stream was released or completed underneath.
        }
        catch (ConnectionException)
        {
        }
        catch (Exception ex) when (IsPerStreamFailure(ex))
        {
        }
    }

    private void ApplySettings(byte[] payload)
    {
        int index = 0;
        while (index < payload.Length)
        {
            long identifier = QuicVariableLengthInteger.Decode(payload, ref index);
            long value = QuicVariableLengthInteger.Decode(payload, ref index);
            _peerSettings.Set(identifier, value);
        }
    }

    /// <summary>
    /// Reads a single QUIC variable-length integer off the pipe, buffering
    /// across reads until a complete integer is available. Returns
    /// <see langword="null"/> when the stream ends before any byte arrives.
    /// </summary>
    private static async Task<long?> ReadVarintAsync(PipeReader reader, CancellationToken cancellationToken)
    {
        while (true)
        {
            ReadResult result = await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
            ReadOnlySequence<byte> buffer = result.Buffer;

            if (QuicVariableLengthInteger.TryDecode(buffer, out long value, out SequencePosition consumed))
            {
                reader.AdvanceTo(consumed);
                return value;
            }

            if (result.IsCompleted)
            {
                if (buffer.IsEmpty)
                {
                    // Clean end of stream before the next integer began.
                    reader.AdvanceTo(buffer.End);
                    return null;
                }

                // Bytes remain but cannot form a complete integer.
                reader.AdvanceTo(buffer.End);
                throw new EndOfStreamException(ErrorMessages.QuicVariableLengthIntegerWasIncomplete);
            }

            // Not enough buffered yet; mark everything examined so the next
            // read waits for more bytes rather than returning the same span.
            reader.AdvanceTo(buffer.Start, buffer.End);
        }
    }

    /// <summary>
    /// Reads exactly <paramref name="length"/> bytes off the pipe, buffering
    /// across reads. Throws <see cref="EndOfStreamException"/> when the stream
    /// ends first. Callers bound <paramref name="length"/> before calling.
    /// </summary>
    private static async Task<byte[]> ReadExactAsync(PipeReader reader, int length, CancellationToken cancellationToken)
    {
        if (length == 0)
        {
            return Array.Empty<byte>();
        }

        while (true)
        {
            ReadResult result = await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
            ReadOnlySequence<byte> buffer = result.Buffer;

            if (buffer.Length >= length)
            {
                ReadOnlySequence<byte> slice = buffer.Slice(0, length);
                byte[] payload = slice.ToArray();
                reader.AdvanceTo(slice.End);
                return payload;
            }

            if (result.IsCompleted)
            {
                reader.AdvanceTo(buffer.End);
                throw new EndOfStreamException(ErrorMessages.Http3ControlFrameWasTruncated);
            }

            reader.AdvanceTo(buffer.Start, buffer.End);
        }
    }

    /// <summary>
    /// Reads and discards exactly <paramref name="length"/> bytes off the pipe,
    /// buffering across reads. Used to drain the payload of a post-SETTINGS
    /// control frame without allocating a buffer for bytes that are thrown away.
    /// Throws <see cref="EndOfStreamException"/> when the stream ends first.
    /// </summary>
    private static async Task SkipAsync(PipeReader reader, long length, CancellationToken cancellationToken)
    {
        long remaining = length;

        while (remaining > 0)
        {
            ReadResult result = await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
            ReadOnlySequence<byte> buffer = result.Buffer;

            if (buffer.Length >= remaining)
            {
                reader.AdvanceTo(buffer.GetPosition(remaining));
                return;
            }

            if (result.IsCompleted)
            {
                reader.AdvanceTo(buffer.End);
                throw new EndOfStreamException(ErrorMessages.Http3ControlFrameWasTruncated);
            }

            remaining -= buffer.Length;
            reader.AdvanceTo(buffer.End);
        }
    }

    /// <summary>
    /// Accepts the next inbound QUIC stream on this multiplexed connection.
    /// QUIC-level failures (peer aborted the connection, the multiplexed
    /// connection was disposed) and cancellation signal connection
    /// termination so the accept loop exits without throwing into the
    /// caller's <c>await foreach</c>.
    /// </summary>
    private async Task<StreamAcceptOutcome> TryAcceptInboundAsync(CancellationToken cancellationToken)
    {
        try
        {
            IMultiplexedConnectionStream streamConnection = await _connection.AcceptStreamAsync(cancellationToken).ConfigureAwait(false);
            return new StreamAcceptOutcome(streamConnection, terminate: false);
        }
        catch (OperationCanceledException)
        {
            return new StreamAcceptOutcome(streamConnection: null, terminate: true);
        }
        catch (QuicException)
        {
            // The QUIC connection itself is gone — peer aborted, idle
            // timeout, or a transport-level error. No more streams will
            // arrive on this connection.
            return new StreamAcceptOutcome(streamConnection: null, terminate: true);
        }
        catch (ConnectionException)
        {
            // Contract-level abort/reset surfaced by the multiplexed
            // connection (ConnectionAbortedException / ConnectionResetException).
            return new StreamAcceptOutcome(streamConnection: null, terminate: true);
        }
        catch (ObjectDisposedException)
        {
            // The underlying multiplexed connection has been disposed
            // (cooperative shutdown raced this accept).
            return new StreamAcceptOutcome(streamConnection: null, terminate: true);
        }
        catch (Exception ex) when (IsWireLevelFailure(ex))
        {
            return new StreamAcceptOutcome(streamConnection: null, terminate: true);
        }
    }

    /// <summary>
    /// Reads one request stream up to its HEADERS frame and publishes the resulting exchange to the
    /// receive enumeration. Runs concurrently with the accept loop and with every other stream.
    /// Expected failures are handled inside <see cref="ReadRequestHeadAsync"/>; an unexpected one is
    /// forwarded to the enumeration rather than swallowed.
    /// </summary>
    private async Task ProcessRequestStreamAsync(IMultiplexedConnectionStream streamConnection, long requestStreamId, CancellationToken receiveToken)
    {
        Http3Context? context = null;
        Http3RequestResetState resetState = new();

        try
        {
            context = await ReadRequestHeadAsync(streamConnection, requestStreamId, resetState, receiveToken).ConfigureAwait(false);

            if (context is not null && !_readyContexts.Writer.TryWrite(context))
            {
                // The channel is already complete (the enumeration faulted), so nothing will ever
                // dispatch this request.
                await RejectUndispatchedAsync(context).ConfigureAwait(false);
            }
        }
        // Not swallowed: an unexpected fault (a programmer error — for example a request hook that
        // throws something other than HttpRequestRejectedException) is forwarded to the consumer, whose
        // enumeration rethrows it, exactly as when request heads were read inline. The stream can no
        // longer be answered, so it is reset.
        catch (Exception exception)
        {
            ResetStream(streamConnection, new Http3StreamException(Http3ErrorCode.InternalError, ErrorMessages.RequestStreamFailedUnexpectedly, exception), _connection.Id, requestStreamId, resetState);
            _readyContexts.Writer.TryComplete(exception);
        }
        finally
        {
            // #1085 — a stream whose head yielded no exchange is no longer in flight; one that yielded an
            // exchange is, until the exchange ends (EndExchange). Only the end of an exchange moves the
            // keep-alive deadline.
            if (context is null)
            {
                CloseRequestStreamPriority(requestStreamId);
                EndRequestStream(exchangeEnded: false);
            }

            EndStreamWork();
        }
    }

    /// <summary>
    /// Reads a request stream up to its HEADERS frame (RFC 9114 §4.1), decodes the field section, and
    /// builds the exchange — without waiting for any of the request body, which
    /// <see cref="Http3RequestBodyStream"/> reads lazily. Handles every expected failure itself and
    /// returns <see langword="null"/> when the stream yields no exchange: a stream error resets the
    /// stream, a connection error aborts the connection.
    /// </summary>
    private async Task<Http3Context?> ReadRequestHeadAsync(IMultiplexedConnectionStream streamConnection, long requestStreamId, Http3RequestResetState resetState, CancellationToken receiveToken)
    {
        // The head read — and a request hook that reads the body before dispatch — observes connection
        // teardown, so a stream whose HEADERS never arrive cannot hold up the accept loop's drain.
        using CancellationTokenSource headCancellation =
            CancellationTokenSource.CreateLinkedTokenSource(receiveToken, _teardownSource.Token);
        CancellationToken cancellationToken = headCancellation.Token;
        Http3RequestStreamReader reader = new(streamConnection.Input, streamConnection);

        // #1085 — the request head must arrive, and decode, within RequestHeadersTimeout of the stream's
        // acceptance — which QUIC makes the arrival of its first octets. The deadline is disarmed once the
        // field section has decoded; a deadline that fired by then still rejects the request.
        bool headersTimed = _limits.RequestHeadersTimeout != Timeout.InfiniteTimeSpan;
        if (headersTimed)
        {
            headCancellation.CancelAfter(_limits.RequestHeadersTimeout);
        }

        try
        {
            byte[]? fieldSection = await reader.ReadHeaderSectionAsync(_limits.MaxRequestHeadersFrameSize, cancellationToken).ConfigureAwait(false);

            if (fieldSection is null)
            {
                // RFC 9114 §8.1 — the stream ended cleanly without a request head.
                ResetRequestStream(
                    streamConnection,
                    requestStreamId,
                    resetState,
                    new Http3StreamException(Http3ErrorCode.RequestIncomplete, ErrorMessages.Http3RequestStreamEndedBeforeItsHeadersFrame),
                    abandonsReading: false);
                return null;
            }

            List<(string Name, string Value)> fields = await DecodeFieldSectionAsync(fieldSection, requestStreamId, cancellationToken).ConfigureAwait(false);

            if (headersTimed)
            {
                headCancellation.CancelAfter(Timeout.InfiniteTimeSpan);
                cancellationToken.ThrowIfCancellationRequested();
            }

            return await CreateContextAsync(streamConnection, reader, requestStreamId, resetState, fields, receiveToken, cancellationToken).ConfigureAwait(false);
        }
        catch (Http3StreamException exception)
        {
            // RFC 9114 §4.1.2 / §7.1 — a malformed request head or an oversized HEADERS frame is a
            // stream error: reset this stream; the connection keeps serving its other streams.
            ResetRequestStream(streamConnection, requestStreamId, resetState, exception, abandonsReading: !reader.IsCompleted);
            return null;
        }
        catch (Http3LimitExceededException exception)
        {
            // RFC 9114 §4.2.2 — the request head decodes past SETTINGS_MAX_FIELD_SECTION_SIZE. The decoder
            // stopped at the field that crossed it, and the request never became an exchange, so the
            // transport answers 431 itself. The reader sits just past the HEADERS frame, so what follows is
            // refused like any unread request (STOP_SENDING unless its FIN is already buffered); the connection keeps serving its other streams.
            Http3RequestBodyStream remainder = new(
                this,
                reader,
                streamConnection,
                requestStreamId,
                resetState,
                new HttpTrailerCollection(isSupported: true),
                declaredContentLength: null,
                isTunnel: false,
                fallbackCap: null,
                maxHeadersFrameSize: _limits.MaxRequestHeadersFrameSize,
                cancellationToken);
            await AnswerRejectedRequestAsync(streamConnection, remainder, requestStreamId, exception.StatusCode, cancellationToken).ConfigureAwait(false);
            return null;
        }
        catch (Http3ConnectionException exception)
        {
            AbortConnection(exception);
            return null;
        }
        catch (QPackException exception)
        {
            // RFC 9204 §2.2 — a QPACK decompression failure corrupts shared dynamic-table state and cannot
            // be isolated to one stream: a connection error.
            AbortConnection(new Http3ConnectionException(exception.ErrorCode, exception.Message, exception));
            return null;
        }
        catch (OperationCanceledException)
        {
            // Teardown (or the consumer's cancellation) before the request was dispatched, or a head that
            // did not arrive within RequestHeadersTimeout (#1085): it was never processed, so RFC 9114
            // §4.1.1 H3_REQUEST_REJECTED tells the peer it may retry it.
            bool timedOut = !receiveToken.IsCancellationRequested && !_teardownSource.IsCancellationRequested;
            ResetRequestStream(
                streamConnection,
                requestStreamId,
                resetState,
                new Http3StreamException(
                    Http3ErrorCode.RequestRejected,
                    timedOut
                        ? ErrorMessages.Http3RequestHeadDidNotArriveWithinTheRequestHeadersTimeout
                        : _requestDispatchStoppedMessage),
                abandonsReading: !reader.IsCompleted);
            return null;
        }
        catch (Exception exception) when (IsRequestStreamFailure(exception))
        {
            // The request stream itself failed — reset by the peer, or torn down with its connection —
            // before its head was complete.
            ResetRequestStream(
                streamConnection,
                requestStreamId,
                resetState,
                new Http3StreamException(Http3ErrorCode.RequestIncomplete, ErrorMessages.Http3RequestStreamFailedBeforeItsRequestHeadWasComplete, exception),
                abandonsReading: !reader.IsCompleted);
            return null;
        }
    }

    /// <summary>
    /// Builds the exchange for a decoded request head: validates the field section, attaches the lazy
    /// request body, runs the request-parse interceptors, and wires the response side.
    /// </summary>
    private async Task<Http3Context?> CreateContextAsync(
        IMultiplexedConnectionStream streamConnection,
        Http3RequestStreamReader reader,
        long requestStreamId,
        Http3RequestResetState resetState,
        List<(string Name, string Value)> fields,
        CancellationToken receiveToken,
        CancellationToken headToken)
    {
        HttpScheme fallbackScheme = _isSecure ? HttpScheme.Https : HttpScheme.Http;
        HttpTrailerCollection trailers = new(isSupported: true);
        TransportHttpRequestHead requestHead;
        long? contentLength;

        try
        {
            requestHead = Http3HeaderCodec.BuildRequestHead(fields, fallbackScheme, trailers, out contentLength);
        }
        catch (InvalidDataException exception)
        {
            // RFC 9114 §4.1.2 — a malformed request is a stream error of type H3_MESSAGE_ERROR.
            throw new Http3StreamException(Http3ErrorCode.MessageError, exception.Message, exception);
        }

        // RFC 9110 §9.3.6 / RFC 9114 §4.4 — a CONNECT's DATA frames are tunnel octets, not a message body.
        bool isConnect = requestHead.Method == HttpMethod.Connect;

        // RFC 9114 §4.1 — content may follow the HEADERS frame until the stream ends. Only an end that already
        // arrived rules it out, and a CONNECT's DATA is tunnel traffic, not content. The stream is probed only
        // when the answer is read: by a request hook (HttpExchangeInterceptorRequestContext.CanHaveBody), or for
        // a request that declared Expect: 100-continue, whose content the body solicits at its first read
        // unless there is none (RFC 9110 §10.1.1, #1385).
        bool expectsContinue = !isConnect && HttpInterimResponseRules.ExpectsContinue(requestHead.Headers);
        bool endBuffered = !isConnect && (_requestInterceptors.Length > 0 || expectsContinue) && reader.IsEndBuffered();
        // A declared body that is missing at FIN still needs late content-length validation and the
        // host's client-fault gate. Its known end does not warrant soliciting more content with 100.
        bool canHaveBody = !isConnect && (contentLength > 0 || !endBuffered);

        Http3RequestBodyStream body = new(
            this,
            reader,
            streamConnection,
            requestStreamId,
            resetState,
            trailers,
            contentLength,
            isConnect,
            _limits.MaxRequestBodySize,
            maxHeadersFrameSize: _limits.MaxRequestHeadersFrameSize,
            headToken,
            _limits.MinRequestBodyDataRate,
            _timeProvider,
            solicitContinue: expectsContinue && !endBuffered);
        requestHead = requestHead with { Body = body };

        HttpConnectionInfo connectionInfo = HttpTlsConnectionInfo.Create(streamConnection.LocalEndPoint, streamConnection.RemoteEndPoint, _tls, _connection.Id);

        // Like HTTP/2's frame pump, run the CPU-only parse hooks inline. A hook with long-running
        // work must offload it explicitly; registering a synchronous feature must not schedule
        // a work item for every request.

        // Request-parse interceptor phase — the HTTP/3 analogue of the HTTP/1.1 invocation point,
        // run as the request head is assembled. RFC 9110 §9.3.6 — a CONNECT's post-head octets are
        // tunnel traffic, so its body hooks are skipped (head hooks still run). The hook-populated
        // feature collection and the (possibly wrapped) body flow into the exchange through the
        // Http3Context constructor; zero interceptors keeps the pre-seam fast path. The returned cap
        // is not used here: the lazy body freezes and enforces the knob at its first read.
        HttpRequestInterceptionResult interception;
        try
        {
            interception = await HttpRequestInterceptorPipeline.InterceptAsync(
                _requestInterceptors,
                HttpVersion.Http30,
                requestHead,
                connectionInfo,
                _limits,
                isConnect,
                canHaveBody,
                _featureCapacity).ConfigureAwait(false);
        }
        catch (HttpRequestRejectedException exception)
        {
            if (body.ClientFaultStatusCode is null)
            {
                ReportRequestRejected(requestStreamId, exception.StatusCode);
            }

            // A request-parse interceptor refused this request before it was dispatched (the pipeline
            // already disposed the partial body-wrapper chain and hook-attached features). RFC 9114
            // §4.1.1 — the server refused it without application processing, so the stream is reset with
            // H3_REQUEST_REJECTED rather than answered with the h1-style status response. The QUIC
            // connection and its other streams are unaffected.
            ResetRequestStream(
                streamConnection,
                requestStreamId,
                resetState,
                new Http3StreamException(Http3ErrorCode.RequestRejected, ErrorMessages.RequestInterceptorRejectedTheRequest),
                abandonsReading: !reader.IsCompleted);
            return null;
        }
        catch (Http3LimitExceededException exception)
        {
            // A request hook read the body before dispatch and the body was rejected: over the cap (413),
            // below the minimum data rate (408), or with a trailer section over the field-section limit
            // (431). The request never became an exchange, so the transport answers the rejection itself
            // and then stops reading the request stream.
            await AnswerRejectedRequestAsync(streamConnection, body, requestStreamId, exception.StatusCode, headToken).ConfigureAwait(false);
            return null;
        }

        Http3Context context = new(
            requestHead with { Body = interception.Body },
            connectionInfo,
            receiveToken,
            streamConnection,
            requestStreamId,
            body,
            _featureCapacity,
            interception.Features,
            _connection.ConnectionClosed)
        {
            AddedResponseInterceptors = interception.ResponseInterceptors,
            RequestInterception = interception.Context,
            Connection = this,
        };
        body.AttachOwner(context);

        // RFC 9218 §4 — the request's Priority header sets the effective priority.
        // Parsing is tolerant: a malformed value leaves the default (urgency 3,
        // non-incremental) in place.
        if (requestHead.Headers.TryGetValue(HttpHeaderKey.Priority, out HttpHeaderValue priorityValue)
            && HttpPriority.TryParse(priorityValue, out HttpPriority headerPriority))
        {
            context.EffectivePriority = headerPriority;
        }

        // Expose the raw DATA-frame response body sink (over the QUIC stream, whose flow control
        // provides backpressure) and the exchange control to registered response interceptors so
        // feature packages (streaming / SSE, interim responses) can wrap them — without this
        // transport depending on any of those packages. The control's interim writes emit an
        // additional HEADERS frame on this request stream ahead of the final one (RFC 9114 §4.1);
        // its abort resets this stream, leaving the QUIC connection's other streams intact.
        context.Response.ConfigureDate(_responseHeaderPolicy);
        IHttpExchangeInterceptor[] responseInterceptors = context.ResolveResponseInterceptors(_responseInterceptors);

        if (responseInterceptors.Length > 0)
        {
            context.RunResponseInterceptors(
                responseInterceptors,
                new Http3ResponseBodyStream(context, this),
                new Http3ExchangeControl(this, context));
        }

        return context;
    }

    /// <summary>
    /// Decodes one QPACK field section received on a request stream — its header section, or its trailer
    /// section — static-only or against the dynamic table. With the dynamic table, a section that
    /// referenced it is acknowledged on the decoder stream once decoded (RFC 9204 §4.4.1), and a section
    /// whose decode was abandoned is cancelled (RFC 9204 §4.4.2), both keyed on
    /// <paramref name="requestStreamId"/>.
    /// </summary>
    /// <param name="fieldSection">The encoded field section (a HEADERS frame payload).</param>
    /// <param name="requestStreamId">The request stream's wire ID.</param>
    /// <param name="cancellationToken">A token to cancel the decode (including a blocked wait).</param>
    /// <returns>The decoded field lines, in wire order.</returns>
    /// <exception cref="Http3StreamException">
    /// Thrown (<c>H3_MESSAGE_ERROR</c>) when the encoding is malformed but the shared decoder state is
    /// intact — the stream is reset and the connection survives.
    /// </exception>
    /// <exception cref="Http3LimitExceededException">
    /// Thrown (<c>431</c>) when the decoded section exceeds <see cref="Http3QPackOptions.MaxFieldSectionSize"/>
    /// (RFC 9114 §4.2.2). The decoder stops at the field that crosses it; the caller answers or resets
    /// the stream, and the connection survives.
    /// </exception>
    /// <exception cref="QPackException">Thrown on a QPACK connection error (RFC 9204 §2.2).</exception>
    internal async ValueTask<List<(string Name, string Value)>> DecodeFieldSectionAsync(
        byte[] fieldSection,
        long requestStreamId,
        CancellationToken cancellationToken)
    {
        if (_decoderState is null)
        {
            try
            {
                // Static-only QPACK decode (dynamic table disabled): the field section resolves against
                // the static table or literals only, and a malformed encoding cannot corrupt state shared
                // with other streams — it is isolated to this stream.
                return QPackFieldSectionDecoder.Decode(fieldSection, _qpackOptions.MaxFieldSectionSize);
            }
            catch (Exception exception) when (IsFieldSectionDecodeFailure(exception))
            {
                throw new Http3StreamException(Http3ErrorCode.MessageError, ErrorMessages.Http3FieldSectionCouldNotBeDecoded, exception);
            }
        }

        // Parse the field section prefix up front, from a single insert-count snapshot, so the "references
        // the dynamic table" decision is known even if the decode is later abandoned — a referencing
        // stream reset before it is acknowledged owes a Stream Cancellation (RFC 9204 §2.2.2.2).
        QPackFieldSectionPrefix prefix;
        try
        {
            prefix = _decoderState.ReadPrefix(fieldSection);
        }
        catch (Exception exception) when (IsFieldSectionDecodeFailure(exception))
        {
            throw new Http3StreamException(Http3ErrorCode.MessageError, ErrorMessages.Http3FieldSectionPrefixCouldNotBeDecoded, exception);
        }

        bool referencedDynamicTable = prefix.RequiredInsertCount > 0;
        bool decodeCompleted = false;

        // Resolving against the dynamic table may block (within the blocked-stream budget) until the
        // referenced insertions arrive (RFC 9204 §2.1.2). The wait is linked to the connection closing, so
        // an encoder-stream abort or the connection going away releases a blocked stream instead of hanging
        // it — and to nothing shorter: a trailer section decoded after the receive enumeration ended still
        // waits for insertions the (connection-lived) encoder drain applies. A request head's own token
        // additionally carries the enumeration's teardown.
        using CancellationTokenSource decodeCancellation =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _connection.ConnectionClosed);

        try
        {
            QPackDecodeResult decode;
            try
            {
                decode = await _decoderState.DecodeRequestAsync(fieldSection, prefix, decodeCancellation.Token).ConfigureAwait(false);
            }
            catch (Exception exception) when (IsFieldSectionDecodeFailure(exception))
            {
                throw new Http3StreamException(Http3ErrorCode.MessageError, ErrorMessages.Http3FieldSectionCouldNotBeDecoded, exception);
            }

            decodeCompleted = true;

            // RFC 9204 §4.4.1 — a field section that referenced the dynamic table is acknowledged on the
            // decoder stream so the peer encoder can advance its Known Received Count (§2.1.1) and evict
            // acknowledged entries. Emitted as soon as the section decodes — ahead of HTTP-layer
            // validation and interceptors — so a request later reset as malformed or refused has still
            // had its section acknowledged (the decode itself succeeded, which is what the
            // acknowledgment attests to).
            if (referencedDynamicTable)
            {
                await SendDecoderInstructionAsync(
                    QPackDecoderInstructionEncoder.SectionAcknowledgment(requestStreamId),
                    cancellationToken).ConfigureAwait(false);
            }

            return decode.Fields;
        }
        finally
        {
            // The section referenced the dynamic table but the decode did not complete (a decompression
            // failure or teardown cancelled it), so no Section Acknowledgment went out: tell the peer
            // encoder to reclaim the outstanding references (RFC 9204 §2.2.2.2 / §4.4.2).
            if (referencedDynamicTable && !decodeCompleted)
            {
                await TrySendStreamCancellationAsync(requestStreamId).ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// Wire-level transport failures that can surface from
    /// <c>AcceptStreamAsync</c>: low-level <see cref="IOException"/>,
    /// <see cref="System.Net.Sockets.SocketException"/>, and unexpected
    /// end-of-stream during the accept handshake. The QUIC connection is
    /// no longer usable when these fire.
    /// </summary>
    private static bool IsWireLevelFailure(Exception exception)
    {
        return exception is EndOfStreamException
            or IOException
            or System.Net.Sockets.SocketException;
    }

    /// <summary>
    /// Per-stream parse and read failures on a unidirectional stream — the frame
    /// reader, the settings and control-frame parsers, a per-stream
    /// <see cref="IOException"/>. These do not invalidate the QUIC connection by
    /// themselves; the callers decide the scope (the control stream is critical).
    /// </summary>
    private static bool IsPerStreamFailure(Exception exception)
    {
        return exception is EndOfStreamException
            or InvalidDataException
            or NotSupportedException
            or OverflowException
            or ArgumentOutOfRangeException
            or IndexOutOfRangeException
            or IOException;
    }

    /// <summary>
    /// A request stream failing underneath its head read: a wire failure (<see cref="IOException"/>,
    /// which includes the QUIC driver's <see cref="QuicException"/>), the in-memory driver's peer abort
    /// (<see cref="ConnectionException"/>), or a stream disposed with its connection.
    /// </summary>
    private static bool IsRequestStreamFailure(Exception exception)
    {
        return exception is IOException
            or ConnectionException
            or ObjectDisposedException;
    }

    /// <summary>
    /// A malformed QPACK encoding the decoder reports without corrupting shared state: the static-only
    /// decoder's failures, and a truncated section on the dynamic path. A
    /// <see cref="QPackException"/> — a connection error — is deliberately not among them.
    /// </summary>
    private static bool IsFieldSectionDecodeFailure(Exception exception)
    {
        return exception is InvalidDataException
            or OverflowException
            or IndexOutOfRangeException
            or ArgumentOutOfRangeException
            or NotSupportedException;
    }

    private readonly struct StreamAcceptOutcome
    {
        public StreamAcceptOutcome(IMultiplexedConnectionStream? streamConnection, bool terminate)
        {
            StreamConnection = streamConnection;
            TerminateConnection = terminate;
        }

        public IMultiplexedConnectionStream? StreamConnection { get; }
        public bool TerminateConnection { get; }
    }

    /// <inheritdoc />
    /// <remarks>
    /// The exchange ends when this call returns or throws, whatever it wrote: from then on its request
    /// stream no longer keeps the connection from going idle (#1085). The one exception is a head or
    /// buffered trailer section the encoder refused (#1183): nothing reached the wire and the response
    /// has not started, so the exchange stays running, and its request stream keeps the connection busy,
    /// until the caller finalizes it again or disposes it — as HTTP/2 keeps the stream's slot.
    /// </remarks>
    public override async ValueTask SendAsync(IHttpContext context, CancellationToken cancellationToken = default)
    {
        if (context is not Http3Context http3Context)
        {
            throw new InvalidOperationException(ErrorMessages.SuppliedContextDoesNotBelongToAnHttp3Connection);
        }

        bool headRefused = false;

        try
        {
            await SendCoreAsync(http3Context, cancellationToken).ConfigureAwait(false);
        }
        catch (HttpInvalidResponseFieldException) when (!http3Context.HasFinalResponseStarted)
        {
            headRefused = true;
            throw;
        }
        finally
        {
            if (!headRefused)
            {
                EndExchange(http3Context);
            }
        }
    }

    private async ValueTask SendCoreAsync(Http3Context http3Context, CancellationToken cancellationToken)
    {
        Http3RequestBodyStream requestBody = http3Context.RequestBody;

        // The transport already reset this request stream — a stream error surfaced while the request
        // body was read — so there is no stream left to answer on. A malformed body reset it before any
        // response started carries no status at all; the exchange then records the client fault's 400
        // (#1378), the status its control reports and HTTP/1.1 answers a malformed body with, so the host
        // classifies it as the client's fault rather than by the 500 its fault boundary staged.
        if (requestBody.IsReset)
        {
            if (!http3Context.HasFinalResponseStarted && requestBody.ClientFaultStatusCode is { } faultStatus)
            {
                http3Context.Response.StatusCode = faultStatus;
            }

            return;
        }

        // An accepted extended CONNECT tunnel already sent the exchange's only head; end the tunnel
        // rather than write a response (RFC 9220).
        if (http3Context.Tunnel is { } tunnel)
        {
            await FinishTunnelAsync(http3Context, tunnel, cancellationToken).ConfigureAwait(false);
            return;
        }

        // The exchange was aborted (IHttpContext.Cancel / CancelAsync — the
        // directive is Abort). RFC 9114 §4.1 — reset the request stream instead of writing a
        // response; the QUIC connection and its other streams are unaffected.
        if (http3Context.CancelRequested)
        {
            CancelRequestStream(http3Context);
            return;
        }

        // If a response feature streamed to the raw sink, the HEADERS and DATA frames are already
        // on the wire (the BeforeResponseHead hooks fired at the sink's head commit); finalize
        // instead of writing a buffered response.
        if (http3Context.ResponseBodySink is { HasStarted: true } sink)
        {
            await FinishStreamedResponseAsync(http3Context, sink, cancellationToken).ConfigureAwait(false);
            return;
        }

        // RFC 9110 §15.5.14 — the request body exceeded the body-size cap (or, §15.5.9, it arrived below
        // the minimum data rate, #1085; or, RFC 9114 §4.2.2, its trailer section exceeded
        // SETTINGS_MAX_FIELD_SECTION_SIZE) and the final response head has not been committed, so the
        // exchange is answered 413 Content Too Large (or 408 Request Timeout, or 431 Request Header Fields
        // Too Large), whatever the application staged from a request it never fully received — unless it
        // answered that status itself, whose representation is kept. The BeforeResponseHead hooks run
        // after this replacement, so a field one of them adds can still have the head refused (#1183).
        if (requestBody.RejectedStatusCode is { } rejectedStatus && http3Context.Response.StatusCode != rejectedStatus)
        {
            await ReplaceWithStatusOnlyResponseAsync(http3Context, rejectedStatus).ConfigureAwait(false);
        }

        // The final response head is about to be committed on the buffered path — the last
        // mutation point. Fire the BeforeResponseHead lifecycle hooks, then re-read the directive
        // so a hook that aborted the exchange resets the stream instead of writing the head.
        await http3Context.InvokeBeforeResponseHeadAsync(cancellationToken).ConfigureAwait(false);

        if (http3Context.CancelRequested)
        {
            CancelRequestStream(http3Context);
            return;
        }

        // A hook may itself have started the response through the raw sink (its HEADERS frame is
        // then already on the wire) — finalize that response rather than writing a second one.
        if (http3Context.ResponseBodySink is { HasStarted: true } hookStartedSink)
        {
            await FinishStreamedResponseAsync(http3Context, hookStartedSink, cancellationToken).ConfigureAwait(false);
            return;
        }

        // RFC 9110 §15.2 — a 1xx status is never a valid final response status. Interim responses go
        // through the IHttpExchangeControl interim writes; HTTP/3 has no 101 (RFC 9114 §4.2), so the
        // rejection is unconditional.
        HttpInterimResponseRules.EnsureFinalStatusCode(http3Context.Response.StatusCode);

        Stream stream = http3Context.StreamConnection.AsStream();
        byte[] bodyBytes = await ReadBodyAsync(http3Context.Response.Body, cancellationToken).ConfigureAwait(false);

        // RFC 9110 §9.3.2 — a response to HEAD carries the header section a GET would but never
        // content: no DATA frame. A Content-Length the application set is preserved; one is synthesized
        // only from a body the handler actually produced (the GET representation's length). An empty
        // HEAD body gets none, because RFC 9110 §8.6 forbids a Content-Length that differs from what GET
        // would send, and the transport cannot know it — the choice the HTTP/2 path makes too. A 204 or 304
        // ends with its HEADERS frame as well (§15.3.5, §15.4.5): no length is synthesized for either,
        // and the encoder sends none on a 204 (#1073, HttpResponseContentRules).
        //
        // The trailer section and the head are encoded before the commit point. A field that either
        // section cannot carry (#1183, RFC 9114 §4.2) throws here, with nothing on the wire and the
        // response unstarted, so the caller can still send another response. The trailers go first: the
        // encoder is static-only, so the order changes no octet, and the head's encode, which adds a
        // synthesized content-length and withdraws it when the head is refused, is then the last thing
        // that can fail, so a refused trailer section never leaves that length behind for a replacement.
        HttpStatusCode statusCode = http3Context.Response.StatusCode;
        HttpMethod method = http3Context.Request.Method;
        bool carriesContent = HttpResponseContentRules.CarriesContent(statusCode, method);

        // RFC 9114 §4.1 — staged trailers follow the content as a HEADERS frame, before the FIN that
        // ends the response. A response without content carries none, as on HTTP/2.
        byte[]? trailerBlock = carriesContent && http3Context.Response.StagedTrailers is { } trailers
            ? Http3HeaderCodec.EncodeTrailers(trailers)
            : null;

        byte[] headerBlock = HttpResponseContentRules.SynthesizesContentLength(statusCode, method, bodyBytes.Length)
            ? Http3HeaderCodec.EncodeResponseHeaders(http3Context, bodyBytes)
            : Http3HeaderCodec.EncodeResponseHeaders(http3Context);

        // Commit point: from here the final response is on the wire, so the exchange control's
        // probes must report the response as started (no more interim writes). Every write from here is
        // held to the response's minimum data rate (#1384): a peer that grants no QUIC stream or connection
        // credit, or reads below the rate, has the request stream reset instead of holding this writer.
        http3Context.MarkFinalResponseStarted();

        // #1385 — an automatic 100 Continue a leaked body read is still writing goes out whole, first.
        await ClaimFinalResponseHeadAsync(http3Context, cancellationToken).ConfigureAwait(false);
        await WriteResponseFrameAsync(http3Context, stream, Http3FrameType.Headers, headerBlock, cancellationToken).ConfigureAwait(false);

        if (bodyBytes.Length > 0 && carriesContent)
        {
            await WriteResponseFrameAsync(http3Context, stream, Http3FrameType.Data, bodyBytes, cancellationToken).ConfigureAwait(false);
        }

        if (trailerBlock is not null)
        {
            await WriteResponseFrameAsync(http3Context, stream, Http3FrameType.Headers, trailerBlock, cancellationToken).ConfigureAwait(false);
        }

        await FlushResponseAsync(http3Context, stream, cancellationToken).ConfigureAwait(false);

        // RFC 9114 §4.1 — the complete response is flushed, so the rest of an unread request is refused
        // with STOP_SENDING(H3_NO_ERROR) — abort reading, finish the response, then end the sending
        // direction. The FIN ends the write side alone (#1330), so the order is the RFC's, not the driver's.
        http3Context.StopObservingStreamClosure();
        requestBody.RefuseRemainder();

        // RFC 9114 §4.1 — an HTTP/3 response body is delimited by the request stream's end, so the
        // response is not complete on the wire until the server ends its write side. End it now, with
        // the response fully flushed: a real .NET HTTP/3 client stays in ReadResponseContentAsync until
        // this FIN arrives, and if the stream is left dangling until connection teardown the client
        // surfaces the teardown as H3_CLOSED_CRITICAL_STREAM (0x104) instead of completing the response.
        CompleteResponseStreamWrites(http3Context.StreamConnection);
        StopReadingRequestStream(requestBody, http3Context.StreamId);

        await http3Context.InvokeAfterResponseAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Finishes a response the application, or a hook, started through the raw sink: its head is on the
    /// wire, so it is completed with the stream's FIN — unless the transport rejected the request body
    /// (#1084).
    /// </summary>
    /// <remarks>
    /// A body over the size cap (RFC 9110 §15.5.14), below the minimum data rate (§15.5.9, #1085), or with
    /// a trailer section over the field-section limit is answered with its status while the head is
    /// uncommitted. Once a streamed head is on the wire no status can follow it, and the response cannot
    /// be whole without the request the server refused to receive, so the stream is reset with
    /// <c>H3_REQUEST_CANCELLED</c> rather than completed: RFC 9114 §4.1.1 — processing began, so never
    /// <c>H3_REQUEST_REJECTED</c>. HTTP/2 resets the stream with <c>CANCEL</c> in the same case. A
    /// streamed response that carries the rejection status itself is complete, and is finished like any
    /// other.
    /// </remarks>
    private async ValueTask FinishStreamedResponseAsync(Http3Context http3Context, HttpResponseBodyStream sink, CancellationToken cancellationToken)
    {
        Http3RequestBodyStream requestBody = http3Context.RequestBody;

        if (requestBody.RejectedStatusCode is { } rejectedStatus && http3Context.Response.StatusCode != rejectedStatus)
        {
            CancelRequestStream(http3Context);
            return;
        }

        await CompleteStreamedResponseAsync(http3Context, sink, cancellationToken).ConfigureAwait(false);
        StopReadingRequestStream(requestBody, http3Context.StreamId);
        await http3Context.InvokeAfterResponseAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Resets an exchange the application cancelled. RFC 9114 §4.1.1 — the application saw the request,
    /// so the server abandons a response after processing began: <c>H3_REQUEST_CANCELLED</c>, never
    /// <c>H3_REQUEST_REJECTED</c> (which would promise the peer the request was not processed).
    /// </summary>
    private void CancelRequestStream(Http3Context http3Context)
    {
        Http3RequestBodyStream requestBody = http3Context.RequestBody;
        bool abandonsReading = !requestBody.IsCompleted;
        requestBody.MarkReset();

        ResetRequestStream(
            http3Context.StreamConnection,
            http3Context.StreamId,
            requestBody.ResetState,
            new Http3StreamException(Http3ErrorCode.RequestCancelled, _exchangeCancelledMessage),
            abandonsReading);
    }

    /// <summary>
    /// Completes a streamed response through its sink: the staged trailer section, if any, then the FIN.
    /// A trailer field the section cannot carry (#1183) is refused before its HEADERS frame is written,
    /// but the head and the body are already out, so the response can be neither completed nor replaced:
    /// the request stream is reset with <c>H3_INTERNAL_ERROR</c> (RFC 9114 §8.1), and the peer never sees
    /// a FIN on a response that lost its trailers. The refusal then propagates.
    /// </summary>
    private async Task CompleteStreamedResponseAsync(Http3Context http3Context, HttpResponseBodyStream sink, CancellationToken cancellationToken)
    {
        try
        {
            await sink.CompleteAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (HttpInvalidResponseFieldException)
        {
            Http3RequestBodyStream requestBody = http3Context.RequestBody;
            bool abandonsReading = !requestBody.IsCompleted;
            requestBody.MarkReset();

            ResetRequestStream(
                http3Context.StreamConnection,
                http3Context.StreamId,
                requestBody.ResetState,
                new Http3StreamException(Http3ErrorCode.InternalError, ErrorMessages.ResponseSTrailerSectionCouldNotBeSent),
                abandonsReading);

            throw;
        }
    }

    /// <summary>
    /// Stops reading a request stream once its complete response is on the wire (RFC 9114 §4.1): an
    /// unread remainder of the request is refused with <c>STOP_SENDING(H3_NO_ERROR)</c>, if
    /// <see cref="Http3RequestBodyStream.RefuseRemainder"/> has not already refused it, and the stream's
    /// input is released. With the dynamic table enabled, abandoning the stream before its end also emits
    /// a Stream Cancellation (RFC 9204 §4.4.2), since a trailer section left unread will never be
    /// acknowledged.
    /// </summary>
    private void StopReadingRequestStream(Http3RequestBodyStream requestBody, long requestStreamId)
    {
        if (!requestBody.IsCompleted && _decoderState is not null)
        {
            _ = TrySendStreamCancellationAsync(requestStreamId);
        }

        requestBody.StopReading();
    }

    /// <summary>
    /// Replaces the application's staged (uncommitted) response with a bodyless one carrying
    /// <paramref name="statusCode"/>: the headers and trailers are cleared and the staged body is
    /// released.
    /// </summary>
    private static async ValueTask ReplaceWithStatusOnlyResponseAsync(Http3Context http3Context, HttpStatusCode statusCode)
    {
        TransportHttpResponse response = http3Context.Response;
        Stream staged = response.Body;

        response.StatusCode = statusCode;
        response.Headers.Clear();
        response.StagedTrailers?.Clear();
        response.Body = new MemoryStream();

        // The exchange disposes only the body it ends up holding, so release the one it no longer holds.
        await staged.DisposeAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// Answers a request that was refused before it became an exchange with a bodyless final response,
    /// ends the response direction, and stops reading the request stream (RFC 9114 §4.1). Best-effort:
    /// a peer that is already gone leaves nothing to answer.
    /// </summary>
    private async Task AnswerRejectedRequestAsync(
        IMultiplexedConnectionStream streamConnection,
        Http3RequestBodyStream requestBody,
        long requestStreamId,
        HttpStatusCode statusCode,
        CancellationToken cancellationToken)
    {
        // Body rejections already reported the first client-fault latch; a rejected head has none.
        if (requestBody.ClientFaultStatusCode is null)
        {
            ReportRequestRejected(requestStreamId, statusCode);
        }

        try
        {
            Stream stream = streamConnection.AsStream();
            byte[] headerBlock = Http3HeaderCodec.EncodeStatusOnlyResponseHeaders(statusCode, _responseHeaderPolicy.GetDateHeader());

            await WriteFrameAsync(stream, Http3FrameType.Headers, headerBlock, cancellationToken).ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            requestBody.RefuseRemainder();
            CompleteResponseStreamWrites(streamConnection);
        }
        catch (OperationCanceledException)
        {
            // Teardown while answering: the connection is going away.
        }
        catch (InvalidOperationException)
        {
            // ObjectDisposedException included: the stream was released underneath.
        }
        catch (ConnectionException)
        {
        }
        catch (Exception exception) when (IsWireLevelFailure(exception))
        {
        }

        StopReadingRequestStream(requestBody, requestStreamId);
    }

    /// <summary>
    /// Rejects a request that was assembled but will never be handed to the application — the consumer
    /// stopped enumerating first. RFC 9114 §4.1.1 — it was not processed, so the stream is reset with
    /// <c>H3_REQUEST_REJECTED</c> (the peer may retry it) and the exchange is disposed.
    /// </summary>
    private async Task RejectUndispatchedAsync(Http3Context context)
    {
        Http3RequestBodyStream requestBody = context.RequestBody;
        bool abandonsReading = !requestBody.IsCompleted;
        requestBody.MarkReset();

        ResetRequestStream(
            context.StreamConnection,
            context.StreamId,
            requestBody.ResetState,
            new Http3StreamException(Http3ErrorCode.RequestRejected, _requestDispatchStoppedMessage),
            abandonsReading);

        await context.DisposeAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// Ends the request stream's write side once the final response is fully written, emitting the
    /// graceful QUIC FIN that delimits an HTTP/3 response body (RFC 9114 §4.1). Completing the
    /// connection's outbound <see cref="PipeWriter"/> is the <see cref="IMultiplexedConnectionStream"/> contract's
    /// documented half-close signal: the read side stays open until the request stream stops reading
    /// (#1330). Best-effort: the response bytes are already flushed to the transport, so a teardown race
    /// that disposes the stream underneath the completion is benign.
    /// </summary>
    /// <param name="streamConnection">The request stream whose write side is ended.</param>
    private static void CompleteResponseStreamWrites(IMultiplexedConnectionStream streamConnection)
    {
        try
        {
            streamConnection.Output.Complete();
        }
        catch (InvalidOperationException)
        {
            // ObjectDisposedException derives from InvalidOperationException: a concurrent connection
            // abort tore the stream down after the response flushed, so the FIN is moot.
        }
        catch (Exception ex) when (IsWireLevelFailure(ex))
        {
            // Completing flushes any residual bytes into the QUIC stream, which raises a wire-level
            // failure (QuicException is an IOException) when the stream/connection is already gone.
            // The response is already delivered, so the missed FIN rides on the connection close.
        }
    }

    /// <summary>
    /// Writes an interim (<c>1xx</c>) response as an additional QPACK-encoded HEADERS frame on the
    /// exchange's request stream, ahead of the final HEADERS frame (RFC 9114 §4.1). The field section
    /// carries the <c>1xx</c> <c>:status</c> and the supplied fields with no <c>Content-Length</c>. The
    /// request stream is single-writer for the response direction, so the interim frame simply
    /// precedes the final frames on the same QUIC stream; QUIC flow control provides backpressure.
    /// </summary>
    /// <param name="http3Context">The exchange whose interim response is emitted.</param>
    /// <param name="statusCode">The interim status code (validated by the caller to be 1xx, not 101).</param>
    /// <param name="headers">The interim response fields, or <see langword="null"/> for none.</param>
    /// <param name="cancellationToken">A token to cancel the write.</param>
    internal Task WriteInterimResponseAsync(
        Http3Context http3Context,
        HttpStatusCode statusCode,
        IHttpHeaderCollection? headers,
        CancellationToken cancellationToken)
    {
        return WriteInterimResponseWithinRateAsync(http3Context, http3Context.StreamConnection, statusCode, headers, cancellationToken);
    }

    /// <summary>
    /// Writes an interim (<c>1xx</c>) response on <paramref name="streamConnection"/>, a request stream
    /// whose final response has not started — the path <see cref="WriteInterimResponseAsync(Http3Context, HttpStatusCode, IHttpHeaderCollection?, CancellationToken)"/>
    /// takes, and the automatic <c>100 Continue</c>'s (<see cref="WriteContinueAsync"/>) when no rate holds
    /// it.
    /// </summary>
    /// <param name="streamConnection">The request stream.</param>
    /// <param name="statusCode">The interim status code (validated by the caller to be 1xx, not 101).</param>
    /// <param name="headers">The interim response fields, or <see langword="null"/> for none.</param>
    /// <param name="cancellationToken">A token to cancel the write.</param>
    internal async Task WriteInterimResponseAsync(
        IMultiplexedConnectionStream streamConnection,
        HttpStatusCode statusCode,
        IHttpHeaderCollection? headers,
        CancellationToken cancellationToken)
    {
        PipeWriter output = streamConnection.Output;
        WriteInterimResponseFrame(output, statusCode, headers);
        await output.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Writes the automatic <c>100 Continue</c> with which a request body's first read solicits a withheld
    /// body (RFC 9110 §10.1.1, #1385) on <paramref name="streamConnection"/>. Once the exchange exists, the
    /// flush is held to the minimum data rate its response is held to (#1384): the request's own
    /// (<see cref="HttpExchangeInterceptorRequestContext.MinResponseDataRate"/>), else the listener's. The rate
    /// is read without freezing it (<see cref="TransportHttpContext.PeekResponseDataRate"/>), so the request
    /// may still change it until the response's first write. The <c>100</c> gets a gate of its own, whose
    /// allowance also bounds a final head waiting for it (<see cref="ClaimFinalResponseHeadAsync"/>), without
    /// charging the wait to the final response. A peer that withholds the credit the <c>100</c> needs past the
    /// allowance has the request stream reset with <c>H3_REQUEST_CANCELLED</c> (<see cref="FailSlowResponse"/>),
    /// and the read that solicited it throws the exchange's cancellation. The request-body rate does not
    /// reclaim such a peer: on a driver that stops a stream with a code it stops the receiving direction
    /// (<c>STOP_SENDING</c>), which never releases the flush. Before the exchange exists (a request hook
    /// reading the body), there is no response to hold to a rate, and the read's token alone bounds the
    /// write.
    /// </summary>
    /// <param name="context">The exchange the request stream belongs to, or <see langword="null"/> before dispatch.</param>
    /// <param name="streamConnection">The request stream.</param>
    /// <param name="cancellationToken">The read's token.</param>
    /// <returns>A task that completes once the request stream accepted the <c>100</c>.</returns>
    /// <exception cref="OperationCanceledException">The peer did not take the <c>100</c> at the rate; the exchange was aborted.</exception>
    internal ValueTask WriteContinueAsync(Http3Context? context, IMultiplexedConnectionStream streamConnection, CancellationToken cancellationToken)
        => new(WriteInterimResponseWithinRateAsync(context, streamConnection, HttpStatusCode.Continue, headers: null, cancellationToken));

    // Application interims and automatic 100 use the same rate rules. Each gets its own allowance so
    // a final head waiting on its claim does not double-charge the response gate (#1384, #1428).
    private Task WriteInterimResponseWithinRateAsync(
        Http3Context? context,
        IMultiplexedConnectionStream streamConnection,
        HttpStatusCode statusCode,
        IHttpHeaderCollection? headers,
        CancellationToken cancellationToken)
    {
        return context is null
            ? WriteInterimResponseCoreAsync(null, streamConnection, statusCode, headers, cancellationToken)
            : RunResponseWriteAsync(context, (streamConnection, statusCode, headers),
                static (connection, exchange, state, token) => new ValueTask(connection.WriteInterimResponseCoreAsync(
                    exchange, state.streamConnection, state.statusCode, state.headers, token)), cancellationToken,
                linkExchangeCancellation: false).AsTask();
    }

    private async Task WriteInterimResponseCoreAsync(
        Http3Context? context,
        IMultiplexedConnectionStream streamConnection,
        HttpStatusCode statusCode,
        IHttpHeaderCollection? headers,
        CancellationToken cancellationToken)
    {
        if (context?.PeekResponseDataRate(_limits.MinResponseDataRate) is not { } rate)
        {
            await WriteInterimResponseAsync(streamConnection, statusCode, headers, cancellationToken).ConfigureAwait(false);
            return;
        }

        MinDataRateGate gate = new(rate, _timeProvider);
        PipeWriter output = streamConnection.Output;
        gate.Record(ticksWaited: 0, bytesTransferred: WriteInterimResponseFrame(output, statusCode, headers));

        bool flushed = await MinDataRateWait.TryRunAsync(
            gate,
            output,
            static (output, token) => FlushInterimResponseAsync(output, token),
            cancellationToken).ConfigureAwait(false);

        if (!flushed)
        {
            throw FailSlowResponse(context);
        }
    }

    /// <summary>
    /// Hands an interim response's HEADERS frame to the request stream's writer, unflushed.
    /// </summary>
    /// <returns>The octets handed over.</returns>
    private static int WriteInterimResponseFrame(PipeWriter output, HttpStatusCode statusCode, IHttpHeaderCollection? headers)
    {
        byte[] headerBlock = Http3HeaderCodec.EncodeInterimResponseHeaders(statusCode, headers);

        // RFC 9114 §7.1 — the whole frame is handed to the request stream's writer before the one flush, so
        // a flush the token cancels never leaves part of a frame behind for the final response to follow,
        // and no write blocks the thread waiting for the peer's credit, as a write through the stream adapter
        // would.
        Span<byte> header = output.GetSpan(2 * QuicVariableLengthInteger.MaxLength);
        int headerLength = QuicVariableLengthInteger.Write(header, (long)Http3FrameType.Headers);
        headerLength += QuicVariableLengthInteger.Write(header[headerLength..], headerBlock.Length);
        output.Advance(headerLength);
        output.Write(headerBlock);

        return headerLength + headerBlock.Length;
    }

    private static async ValueTask FlushInterimResponseAsync(PipeWriter output, CancellationToken cancellationToken)
    {
        await output.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Takes the request stream's writer for an exchange's final response head
    /// (<see cref="Http3Context.ClaimFinalResponseHeadAsync"/>) after any active interim response finishes.
    /// The interim's own minimum data rate allowance bounds this wait, including the credit for its
    /// encoded frame. A separate zero-byte allowance here would cancel a valid large interim after only
    /// the grace period (#1428). Caller and exchange cancellation still end the wait. The final response
    /// starts its rate accounting at its first write after the handoff, without charging this wait again.
    /// </summary>
    /// <param name="context">The exchange whose final head is about to be written.</param>
    /// <param name="cancellationToken">The caller's token.</param>
    /// <returns>A task that completes once the head may be written.</returns>
    /// <exception cref="OperationCanceledException">The caller or exchange was cancelled, including an interim that exceeded its rate allowance.</exception>
    internal async ValueTask ClaimFinalResponseHeadAsync(Http3Context context, CancellationToken cancellationToken)
    {
        ThrowIfResponsePeerAborted(context);
        ValueTask claimed = context.ClaimFinalResponseHeadAsync();

        if (claimed.IsCompletedSuccessfully)
        {
            return;
        }

        using CancellationTokenSource cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, context.RequestCancelled);
        try
        {
            await claimed.AsTask().WaitAsync(cancellation.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Preserve the originating token instead of exposing the temporary linked token.
            ThrowIfResponsePeerAborted(context);
            context.RequestCancelled.ThrowIfCancellationRequested();
            cancellationToken.ThrowIfCancellationRequested();
            throw;
        }

        ThrowIfResponsePeerAborted(context);
        context.RequestCancelled.ThrowIfCancellationRequested();
        if (context.RequestBody.IsReset)
        {
            // The interim exceeded its allowance and reset the request stream before releasing the claim.
            throw context.AbortSlowResponse();
        }
    }

    /// <summary>
    /// Writes one frame of a response — its type and length, then its payload — to the request stream,
    /// held to the response's minimum data rate (#1384). The frame header is encoded straight into the
    /// request stream's own buffer (<see cref="PipeWriter.GetSpan"/>), never octet by octet through the
    /// stream adapter, whose synchronous writes flush, so a flush the peer withholds credit for would
    /// block the thread beyond any token; it goes out with the payload's first write, or with the flush
    /// that follows a frame with no payload. With a rate, the payload is handed over in pieces of at most
    /// <see cref="MinDataRateWait.MaxWriteSize"/> octets, each credited as it goes (the header with the
    /// first) and accepted within what is left of the allowance; when the allowance runs out the stream
    /// is reset (<see cref="FailSlowResponse"/>) and the write throws.
    /// </summary>
    /// <param name="context">The exchange whose response is written.</param>
    /// <param name="stream">The request stream, as a stream.</param>
    /// <param name="frameType">The frame type.</param>
    /// <param name="payload">The frame payload.</param>
    /// <param name="cancellationToken">The caller's token.</param>
    /// <returns>A task that completes once the request stream accepted the frame.</returns>
    /// <exception cref="OperationCanceledException">The peer did not accept the frame at the rate; the exchange was aborted.</exception>
    internal ValueTask WriteResponseFrameAsync(Http3Context context, Stream stream, Http3FrameType frameType, ReadOnlyMemory<byte> payload, CancellationToken cancellationToken)
    {
        return RunResponseWriteAsync(context, (stream, frameType, payload),
            static (connection, exchange, state, token) => connection.WriteResponseFrameCoreAsync(
                exchange, state.stream, state.frameType, state.payload, token), cancellationToken);
    }

    private async ValueTask WriteResponseFrameCoreAsync(Http3Context context, Stream stream, Http3FrameType frameType, ReadOnlyMemory<byte> payload, CancellationToken cancellationToken)
    {
        MinDataRateGate? gate = context.GetResponseDataRateGate(_limits.MinResponseDataRate, _timeProvider);

        // RFC 9114 §7.1 — frame = type (varint) + length (varint) + payload.
        PipeWriter output = context.StreamConnection.Output;
        Span<byte> header = output.GetSpan(2 * QuicVariableLengthInteger.MaxLength);
        int headerLength = QuicVariableLengthInteger.Write(header, (long)frameType);
        headerLength += QuicVariableLengthInteger.Write(header[headerLength..], payload.Length);
        output.Advance(headerLength);

        if (gate is null)
        {
            if (!payload.IsEmpty)
            {
                await stream.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
            }

            return;
        }

        int credit = headerLength;
        for (int offset = 0; offset < payload.Length; offset += MinDataRateWait.MaxWriteSize)
        {
            ReadOnlyMemory<byte> piece = payload.Slice(offset, Math.Min(MinDataRateWait.MaxWriteSize, payload.Length - offset));
            await WriteResponseOctetsWithinRateAsync(context, gate, stream, piece, credit + piece.Length, cancellationToken).ConfigureAwait(false);
            credit = 0;
        }

        if (credit > 0)
        {
            // A frame with no payload: its header rides the flush that follows, and counts toward it.
            gate.Record(ticksWaited: 0, bytesTransferred: credit);
        }
    }

    /// <summary>
    /// Flushes a response's request stream, held to the response's minimum data rate (#1384).
    /// </summary>
    /// <param name="context">The exchange whose response is flushed.</param>
    /// <param name="stream">The request stream, as a stream.</param>
    /// <param name="cancellationToken">The caller's token.</param>
    /// <returns>A task that completes once the request stream accepted every octet written.</returns>
    /// <exception cref="OperationCanceledException">The peer did not accept the response at the rate; the exchange was aborted.</exception>
    internal ValueTask FlushResponseAsync(Http3Context context, Stream stream, CancellationToken cancellationToken)
    {
        return RunResponseWriteAsync(context, stream,
            static (connection, exchange, output, token) => connection.FlushResponseCoreAsync(exchange, output, token), cancellationToken);
    }

    private ValueTask FlushResponseCoreAsync(Http3Context context, Stream stream, CancellationToken cancellationToken)
    {
        MinDataRateGate? gate = context.GetResponseDataRateGate(_limits.MinResponseDataRate, _timeProvider);

        return gate is null
            ? new ValueTask(stream.FlushAsync(cancellationToken))
            : WriteResponseOctetsWithinRateAsync(context, gate, stream, ReadOnlyMemory<byte>.Empty, 0, cancellationToken);
    }

    /// <summary>
    /// Hands <paramref name="data"/> to the request stream, or flushes it when <paramref name="data"/> is
    /// empty, crediting <paramref name="credit"/> octets first and bounding the wait by what is left of
    /// the allowance (<see cref="MinDataRateWait"/>).
    /// </summary>
    private async ValueTask WriteResponseOctetsWithinRateAsync(Http3Context context, MinDataRateGate gate, Stream stream, ReadOnlyMemory<byte> data, int credit, CancellationToken cancellationToken)
    {
        gate.Record(ticksWaited: 0, bytesTransferred: credit);

        bool accepted = data.IsEmpty
            ? await MinDataRateWait.TryRunAsync(gate, stream, static (output, token) => new ValueTask(output.FlushAsync(token)), cancellationToken).ConfigureAwait(false)
            : await MinDataRateWait.TryRunAsync(gate, (Output: stream, Data: data), static (state, token) => state.Output.WriteAsync(state.Data, token), cancellationToken).ConfigureAwait(false);

        if (!accepted)
        {
            throw FailSlowResponse(context);
        }
    }

    /// <summary>
    /// Ends an exchange whose peer did not accept its response at the minimum response data rate
    /// (#1384): the request stream is reset in both directions with <c>H3_REQUEST_CANCELLED</c>
    /// (RFC 9114 §4.1.1 — the request was processed, and its response is abandoned), which releases the
    /// QUIC stream and leaves the connection and its other streams serving, and the exchange's
    /// <c>RequestCancelled</c> fires. A peer that withholds connection-level credit (<c>MAX_DATA</c>)
    /// stalls every response, and each is reset as its own allowance runs out.
    /// </summary>
    /// <param name="context">The exchange whose response stalled.</param>
    /// <returns>The exception the stalled write throws.</returns>
    internal OperationCanceledException FailSlowResponse(Http3Context context)
    {
        // Publish the reason before resetting: the reset can wake another response writer at once.
        context.LatchResponseAbortedByClient();
        Http3RequestBodyStream requestBody = context.RequestBody;

        if (!requestBody.IsReset)
        {
            HttpTransportEventSource.Log.ResponseRateAborted(_connection.Id, "3", context.StreamId);
            bool abandonsReading = !requestBody.IsCompleted;
            requestBody.MarkReset();

            ResetRequestStream(
                context.StreamConnection,
                context.StreamId,
                requestBody.ResetState,
                new Http3StreamException(Http3ErrorCode.RequestCancelled, ErrorMessages.PeerDidNotReadTheResponseAtTheConfiguredMinimumDataRate),
                abandonsReading);
        }

        return context.AbortSlowResponse();
    }

    /// <summary>
    /// Bounds final response writes by the exchange's cancellation even when the caller supplied no
    /// token; active interims retain their caller cancellation while observing peer stream closure.
    /// Records a peer's RESET_STREAM or STOP_SENDING before the failure reaches the application.
    /// The raw write alone is guarded: application response encoding and lifecycle hooks keep their
    /// own failures, and local resets and request-body rejections never become response client faults.
    /// </summary>
    internal async ValueTask RunResponseWriteAsync<TState>(
        Http3Context context,
        TState state,
        Func<Http3ConnectionContext, Http3Context, TState, CancellationToken, ValueTask> operation,
        CancellationToken cancellationToken,
        bool linkExchangeCancellation = true)
    {
        ThrowIfResponsePeerAborted(context);
        CancellationToken requestCancelled = context.RequestCancelled;
        // An active interim retains its caller-token contract when the application cancels the
        // exchange (#1428). Stream or parent closure must still release it; final writes also observe local cancel.
        CancellationToken transportCancellation = linkExchangeCancellation
            ? requestCancelled
            : context.StreamConnection.ConnectionClosed;
        transportCancellation.ThrowIfCancellationRequested();
        using CancellationTokenSource? linked = !linkExchangeCancellation
            ? CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, transportCancellation, ConnectionClosed)
            : cancellationToken.CanBeCanceled && cancellationToken != transportCancellation
                ? CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, transportCancellation)
                : null;
        CancellationToken writeToken = linked?.Token ?? transportCancellation;

        try
        {
            await operation(this, context, state, writeToken).ConfigureAwait(false);
            // A pipe may report a completed flush instead of throwing when the peer stopped reading.
            ThrowIfResponsePeerAborted(context);
        }
        catch (Exception exception) when (IsResponsePeerAbort(context, exception))
        {
            throw context.AbortResponseForPeer(exception);
        }
        catch (OperationCanceledException) when (requestCancelled.IsCancellationRequested)
        {
            requestCancelled.ThrowIfCancellationRequested();
            throw;
        }
        catch (OperationCanceledException exception) when (cancellationToken.IsCancellationRequested
            && exception.CancellationToken != cancellationToken)
        {
            throw new OperationCanceledException(exception.Message, exception, cancellationToken);
        }
    }

    private void ThrowIfResponsePeerAborted(Http3Context context)
    {
        if ((context.StreamConnection.ConnectionClosed.IsCancellationRequested || ConnectionClosed.IsCancellationRequested)
            && CanReportResponsePeerAbort(context))
        {
            throw context.AbortResponseForPeer(new OperationCanceledException(context.StreamConnection.ConnectionClosed));
        }
    }

    private bool IsResponsePeerAbort(Http3Context context, Exception exception)
    {
        if (!CanReportResponsePeerAbort(context))
        {
            return false;
        }

        // Native QUIC can throw before its closure callback; the in-memory driver carries the code in
        // ConnectionResetException. Other pipe failures need the stream's closure as supporting evidence.
        return exception is ConnectionResetException
            || (exception is QuicException quic && IsQuicResponseAborted(quic))
            || ((context.StreamConnection.ConnectionClosed.IsCancellationRequested || ConnectionClosed.IsCancellationRequested)
                && exception is IOException or ConnectionException or InvalidOperationException or OperationCanceledException);
    }

    private static bool IsQuicResponseAborted(QuicException exception)
    {
        // Match the driver's peer-closure notification, which runs after the write may have failed:
        // local aborts report OperationAborted; other stream failures include connection loss/timeouts.
        return (OperatingSystem.IsWindows() || OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
            && exception.QuicError != QuicError.OperationAborted;
    }

    private bool CanReportResponsePeerAbort(Http3Context context)
    {
        return !context.CancelRequested
            && !context.RequestBody.IsReset
            && context.RequestBody.ClientFaultStatusCode is null
            && !_localConnectionClose
            && !_receiveCancellation.IsCancellationRequested;
    }

    private static async Task WriteFrameAsync(Stream stream, Http3FrameType frameType, byte[] payload, CancellationToken cancellationToken)
    {
        QuicVariableLengthInteger.Write(stream, (long)frameType);
        QuicVariableLengthInteger.Write(stream, payload.Length);

        if (payload.Length > 0)
        {
            await stream.WriteAsync(payload, 0, payload.Length, cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task<byte[]> ReadBodyAsync(Stream body, CancellationToken cancellationToken)
    {
        if (body is MemoryStream memoryStream)
        {
            return memoryStream.ToArray();
        }

        using MemoryStream copy = new();
        if (body.CanSeek)
        {
            body.Position = 0;
        }

        await body.CopyToAsync(copy, cancellationToken).ConfigureAwait(false);
        return copy.ToArray();
    }
}
