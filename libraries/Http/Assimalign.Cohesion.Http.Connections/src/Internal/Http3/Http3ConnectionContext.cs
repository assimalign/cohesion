using System;
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
    // RFC 9114 §7.2.4 / RFC 9218 §7.2 — the peer's SETTINGS frame and its control-stream PRIORITY_UPDATE
    // frames are read whole before they are applied. Both carry a handful of varints, so a declared
    // length beyond this bound is refused rather than buffered.
    private const int maxControlFramePayloadSize = 16 * 1024;

    // Before a response ends, an unread remainder of its request — up to about one QUIC stream receive
    // window, arriving within the timeout — is read and discarded so the peer's upload finishes normally.
    // Stopping the stream instead would signal STOP_SENDING, whose H3_NO_ERROR code (RFC 9114 §4.1) the
    // connection contract cannot yet carry (see Http3RequestBodyStream.TryDrainAsync).
    private const long requestBodyDrainBudget = 64 * 1024;
    private static readonly TimeSpan _requestBodyDrainTimeout = TimeSpan.FromSeconds(5);

    private readonly IMultiplexedConnection _connection;
    private readonly bool _isSecure;
    // The QUIC handshake's facts, published on every request stream's connection info as the
    // ITlsConnectionInfo facet; null when the connection does not report them (see HttpTlsConnectionInfo).
    private readonly ITlsConnectionInfo? _tls;
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
    private IConnection? _controlStream;
    private IConnection? _decoderStream;
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
    // RFC 9218 §7.2 — the effective priority of request streams the peer has
    // re-prioritized via a control-stream PRIORITY_UPDATE. This is the HTTP/3
    // engine's observable priority state; response ordering across streams is
    // otherwise delegated to the QUIC transport (see docs/DESIGN.md). Guarded
    // because the control-stream drain runs on a background task.
    private readonly Dictionary<long, HttpPriority> _requestStreamPriorities = new();
    private readonly object _priorityLock = new();
    private volatile bool _pushPriorityUpdateRejected;

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
        Http3QPackOptions qpackOptions)
    {
        _connection = connection;
        _isSecure = isSecure;
        _tls = connection as ITlsConnectionInfo;
        _limits = limits;
        _requestInterceptors = requestInterceptors;
        _responseInterceptors = responseInterceptors;
        _qpackOptions = qpackOptions;

        // The dynamic table (and its encoder/decoder instruction streams) is
        // opt-in: with QPACK_MAX_TABLE_CAPACITY = 0 the decoder state is never
        // created and the transport stays on the static-only path.
        _decoderState = qpackOptions.DynamicTableEnabled ? new QPackDecoderState(qpackOptions) : null;
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
            return _requestStreamPriorities.TryGetValue(streamId, out priority);
        }
    }

    /// <summary>
    /// Whether a push PRIORITY_UPDATE (frame type 0xF0701) has been received and
    /// rejected. The server issues no pushes, so such a frame references a push id
    /// that cannot exist (RFC 9218 §7.2 / H3_ID_ERROR).
    /// </summary>
    internal bool PushPriorityUpdateRejected => _pushPriorityUpdateRejected;

    private void RecordRequestStreamPriority(long streamId, HttpPriority priority)
    {
        lock (_priorityLock)
        {
            _requestStreamPriorities[streamId] = priority;
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
        try
        {
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
                    _ = ProcessRequestStreamAsync(streamConnection, requestStreamId, receiveToken);
                }
                else
                {
                    _ = ProcessUnidirectionalStreamAsync(streamConnection, receiveToken);
                }
            }

            // No stream is accepted after this point, so the request-stream count is final; a
            // graceful close waits for it before it announces its GOAWAY boundary.
            _acceptStopped.TrySetResult();

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
            IConnection controlStream = await _connection
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
                IConnection decoderStream = await _connection
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
    /// gate, and the background control-stream and QPACK encoder drains all live until the connection
    /// itself closes (<see cref="IMultiplexedConnection.ConnectionClosed"/>). The drains end on their
    /// own then, and swallow their own failures, so nothing awaits them.
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
        IConnection? controlStream = Volatile.Read(ref _controlStream);
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
    /// A request stream the peer opens after this point is not accepted. It is closed with the QUIC
    /// connection; the GOAWAY tells the peer it was not processed.
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
    /// Raises a connection error (RFC 9114 §8): records it (the first one wins), signals teardown so the
    /// accept loop and every head read in flight stop, and aborts the multiplexed connection with the
    /// error as the reason — which signals <see cref="ConnectionClosed"/>, ending the drains, any blocked
    /// QPACK decode, and every request-body read still in flight. The enumeration ends after the contexts
    /// already published. Called from any stream's processing and from request-body reads; idempotent.
    /// </summary>
    /// <param name="error">The connection error, carrying the code the connection is closed with.</param>
    internal void AbortConnection(Http3ConnectionException error)
    {
        if (Interlocked.CompareExchange(ref _connectionError, error, null) is not null)
        {
            return;
        }

        _teardownSource.Cancel();
        _connection.Abort(error);
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
    /// <param name="reason">The stream error, carrying the RFC 9114 §8.1 code.</param>
    /// <param name="abandonsReading">Whether the stream still had unread octets.</param>
    internal void ResetRequestStream(IConnection streamConnection, long requestStreamId, Http3StreamException reason, bool abandonsReading)
    {
        if (abandonsReading && _decoderState is not null)
        {
            _ = TrySendStreamCancellationAsync(requestStreamId);
        }

        streamConnection.Abort(reason);
    }

    /// <summary>
    /// Processes one peer-initiated unidirectional stream on its own, off the accept loop, and turns a
    /// connection-level violation into a connection error.
    /// </summary>
    private async Task ProcessUnidirectionalStreamAsync(IConnection streamConnection, CancellationToken receiveToken)
    {
        try
        {
            using CancellationTokenSource streamCancellation =
                CancellationTokenSource.CreateLinkedTokenSource(receiveToken, _teardownSource.Token);

            Http3ConnectionException? violation = await TryHandleUnidirectionalStreamAsync(
                streamConnection,
                streamCancellation.Token).ConfigureAwait(false);

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
        // the channel, and the enumeration rethrows it.
        catch (Exception exception)
        {
            _readyContexts.Writer.TryComplete(exception);
        }
        finally
        {
            EndStreamWork();
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
        IConnection streamConnection,
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
            return null;
        }

        if (streamType is null)
        {
            return null;
        }

        switch (streamType.Value)
        {
            case Http3StreamType.Control:
                return await TryHandleControlStreamAsync(reader, cancellationToken).ConfigureAwait(false);

            case Http3StreamType.QPackEncoder:
                // RFC 9204 §4.2 — at most one encoder stream. With the dynamic
                // table disabled (QPACK_MAX_TABLE_CAPACITY = 0) it carries no
                // instructions to process, so accepting it is sufficient. With the
                // table enabled, drain it in the background so its Set Capacity /
                // Insert / Duplicate instructions populate the dynamic table while
                // requests keep being served.
                if (Interlocked.Exchange(ref _qpackEncoderStreamReceived, 1) == 1)
                {
                    return new Http3ConnectionException(
                        Http3ErrorCode.StreamCreationError,
                        "The peer opened a second QPACK encoder stream (RFC 9204 §4.2).");
                }

                if (_decoderState is not null)
                {
                    // Connection-lived: a request body read after the enumeration ends may still wait on
                    // insertions this drain applies. The drain never faults, so it is not awaited.
                    _ = DrainQPackEncoderStreamAsync(reader);
                }

                return null;

            case Http3StreamType.QPackDecoder:
                if (Interlocked.Exchange(ref _qpackDecoderStreamReceived, 1) == 1)
                {
                    return new Http3ConnectionException(
                        Http3ErrorCode.StreamCreationError,
                        "The peer opened a second QPACK decoder stream (RFC 9204 §4.2).");
                }

                return null;

            case Http3StreamType.Push:
                // RFC 9114 §6.2.2 — a client MUST NOT create a push stream;
                // treat it as H3_STREAM_CREATION_ERROR.
                return new Http3ConnectionException(
                    Http3ErrorCode.StreamCreationError,
                    "A client opened a push stream (RFC 9114 §6.2.2).");

            default:
                // RFC 9114 §6.2 — unknown unidirectional stream types are not
                // an error; the recipient may abandon them.
                return null;
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
                "The peer opened a second control stream (RFC 9114 §6.2.1).");
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
                    "The peer's control stream did not open with a SETTINGS frame (RFC 9114 §6.2.1).");
            }

            if (frameLength.Value > maxControlFramePayloadSize)
            {
                return new Http3ConnectionException(
                    Http3ErrorCode.FrameError,
                    $"The peer's SETTINGS frame declares {frameLength.Value} octets, beyond the {maxControlFramePayloadSize}-octet bound.");
            }

            byte[] payload = await ReadExactAsync(reader, (int)frameLength.Value, cancellationToken).ConfigureAwait(false);
            ApplySettings(payload);
        }
        catch (Exception ex) when (IsPerStreamFailure(ex))
        {
            // The control stream is critical; a read/parse failure on the
            // mandatory SETTINGS frame is a connection error (RFC 9114 §7.1).
            return new Http3ConnectionException(
                Http3ErrorCode.FrameError,
                "The peer's SETTINGS frame was truncated or malformed (RFC 9114 §7.1).",
                ex);
        }

        // RFC 9114 §6.2.1 — the control stream stays open for the connection
        // lifetime. Drain any post-SETTINGS frames on a background task so they
        // cannot accumulate unread in the pipe. Connection-lived, and it never
        // faults, so it is not awaited.
        _ = DrainPeerControlStreamAsync(reader);
        return null;
    }

    /// <summary>
    /// Processes post-SETTINGS frames from the peer's control stream for the
    /// connection lifetime (RFC 9114 §7.2). RFC 9218 §7.2 PRIORITY_UPDATE frames
    /// are parsed and applied: a request-stream update (0xF0700) records the
    /// referenced stream's effective priority; a push update (0xF0701) is rejected
    /// (the server issues no pushes) and stops the drain. GOAWAY (§7.2.6) and
    /// MAX_PUSH_ID (§7.2.7) are read but inert in this subset, and every other
    /// frame is discarded. Processing prevents unread control frames from
    /// accumulating in the pipe. The loop runs for the connection's lifetime —
    /// not the receive enumeration's — and stops on end-of-stream, the connection
    /// closing, or a per-stream parse failure; it never throws.
    /// </summary>
    private async Task DrainPeerControlStreamAsync(PipeReader reader)
    {
        CancellationToken cancellationToken = _connection.ConnectionClosed;

        try
        {
            while (true)
            {
                long? frameType = await ReadVarintAsync(reader, cancellationToken).ConfigureAwait(false);
                if (frameType is null)
                {
                    // Clean end of the peer control stream — nothing more to drain.
                    break;
                }

                long? frameLength = await ReadVarintAsync(reader, cancellationToken).ConfigureAwait(false);
                if (frameLength is null)
                {
                    break;
                }

                int length = checked((int)frameLength.Value);

                if (frameType.Value == (long)Http3FrameType.PriorityUpdateRequest)
                {
                    if (length > maxControlFramePayloadSize)
                    {
                        // An oversized PRIORITY_UPDATE is malformed: stop draining, exactly as for any
                        // other malformed post-SETTINGS frame (below).
                        break;
                    }

                    // RFC 9218 §7.2 — read the payload (Prioritized Element ID +
                    // Priority Field Value) and apply it to the referenced stream.
                    byte[] priorityPayload = await ReadExactAsync(reader, length, cancellationToken).ConfigureAwait(false);
                    if (Http3PriorityUpdate.TryParse(priorityPayload, out long prioritizedStreamId, out HttpPriority priority))
                    {
                        RecordRequestStreamPriority(prioritizedStreamId, priority);
                    }

                    continue;
                }

                if (frameType.Value == (long)Http3FrameType.PriorityUpdatePush)
                {
                    // RFC 9218 §7.2 — the server advertises no push capacity, so a
                    // push PRIORITY_UPDATE references a push id that cannot exist.
                    // Reject it consistently with the server-push de-scope: record
                    // the rejection and stop draining so connection teardown closes
                    // the QUIC connection. (Strict HTTP/3 would signal H3_ID_ERROR;
                    // this drain keeps its parse-and-discard posture — see docs/DESIGN.md.)
                    await SkipAsync(reader, length, cancellationToken).ConfigureAwait(false);
                    _pushPriorityUpdateRejected = true;
                    break;
                }

                await SkipAsync(reader, length, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // The connection closed — stop draining.
        }
        catch (Exception ex) when (IsPerStreamFailure(ex))
        {
            // A malformed post-SETTINGS control frame. Strict HTTP/3 would treat
            // this as a connection error; in this parse-and-discard subset the
            // drain stops and connection teardown closes the QUIC connection.
        }
        catch (InvalidOperationException)
        {
            // ObjectDisposedException included: the stream was released with its connection.
        }
        catch (ConnectionException)
        {
            // The in-memory driver's abort of the stream underneath the drain.
        }
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
                            "The QPACK encoder stream ended in the middle of an instruction.");
                    }

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
        catch (Exception ex) when (IsPerStreamFailure(ex))
        {
            // A wire failure on the encoder stream; teardown closes the connection.
        }
        catch (InvalidOperationException)
        {
            // ObjectDisposedException included: the encoder or decoder stream was released with its
            // connection.
        }
        catch (ConnectionException)
        {
            // The in-memory driver's abort of a stream underneath the drain.
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
                throw new EndOfStreamException("The QUIC variable-length integer was incomplete.");
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
                throw new EndOfStreamException("An HTTP/3 control frame was truncated.");
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
    private static async Task SkipAsync(PipeReader reader, int length, CancellationToken cancellationToken)
    {
        int remaining = length;

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
                throw new EndOfStreamException("An HTTP/3 control frame was truncated.");
            }

            remaining -= (int)buffer.Length;
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
            IConnection streamConnection = await _connection.AcceptStreamAsync(cancellationToken).ConfigureAwait(false);
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
    private async Task ProcessRequestStreamAsync(IConnection streamConnection, long requestStreamId, CancellationToken receiveToken)
    {
        try
        {
            Http3Context? context = await ReadRequestHeadAsync(streamConnection, requestStreamId, receiveToken).ConfigureAwait(false);

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
            streamConnection.Abort(new Http3StreamException(Http3ErrorCode.InternalError, "The request stream failed unexpectedly.", exception));
            _readyContexts.Writer.TryComplete(exception);
        }
        finally
        {
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
    private async Task<Http3Context?> ReadRequestHeadAsync(IConnection streamConnection, long requestStreamId, CancellationToken receiveToken)
    {
        // The head read — and a request hook that reads the body before dispatch — observes connection
        // teardown, so a stream whose HEADERS never arrive cannot hold up the accept loop's drain.
        using CancellationTokenSource headCancellation =
            CancellationTokenSource.CreateLinkedTokenSource(receiveToken, _teardownSource.Token);
        CancellationToken cancellationToken = headCancellation.Token;
        Http3RequestStreamReader reader = new(streamConnection.Input);

        try
        {
            byte[]? fieldSection = await reader.ReadHeaderSectionAsync(_limits.MaxRequestHeadersFrameSize, cancellationToken).ConfigureAwait(false);

            if (fieldSection is null)
            {
                // RFC 9114 §8.1 — the stream ended cleanly without a request head.
                ResetRequestStream(
                    streamConnection,
                    requestStreamId,
                    new Http3StreamException(Http3ErrorCode.RequestIncomplete, "The HTTP/3 request stream ended before its HEADERS frame."),
                    abandonsReading: false);
                return null;
            }

            List<(string Name, string Value)> fields = await DecodeFieldSectionAsync(fieldSection, requestStreamId, cancellationToken).ConfigureAwait(false);
            return await CreateContextAsync(streamConnection, reader, requestStreamId, fields, receiveToken, cancellationToken).ConfigureAwait(false);
        }
        catch (Http3StreamException exception)
        {
            // RFC 9114 §4.1.2 / §7.1 — a malformed request head or an oversized HEADERS frame is a
            // stream error: reset this stream; the connection keeps serving its other streams.
            ResetRequestStream(streamConnection, requestStreamId, exception, abandonsReading: !reader.IsCompleted);
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
            // Teardown (or the consumer's cancellation) before the request was dispatched: it was never
            // processed, so RFC 9114 §4.1.1 H3_REQUEST_REJECTED tells the peer it may retry it.
            ResetRequestStream(
                streamConnection,
                requestStreamId,
                new Http3StreamException(Http3ErrorCode.RequestRejected, "The connection stopped before the request was dispatched."),
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
                new Http3StreamException(Http3ErrorCode.RequestIncomplete, "The HTTP/3 request stream failed before its request head was complete.", exception),
                abandonsReading: !reader.IsCompleted);
            return null;
        }
    }

    /// <summary>
    /// Builds the exchange for a decoded request head: validates the field section, attaches the lazy
    /// request body, runs the request-parse interceptors, and wires the response side.
    /// </summary>
    private async Task<Http3Context?> CreateContextAsync(
        IConnection streamConnection,
        Http3RequestStreamReader reader,
        long requestStreamId,
        List<(string Name, string Value)> fields,
        CancellationToken receiveToken,
        CancellationToken headToken)
    {
        HttpScheme fallbackScheme = _isSecure ? HttpScheme.Https : HttpScheme.Http;
        HttpTrailerCollection trailers = new(isSupported: true);
        TransportHttpRequestHead requestHead;
        string? extendedConnectProtocol;
        long? contentLength;

        try
        {
            requestHead = Http3HeaderCodec.BuildRequestHead(fields, fallbackScheme, trailers, out extendedConnectProtocol, out contentLength);
        }
        catch (InvalidDataException exception)
        {
            // RFC 9114 §4.1.2 — a malformed request is a stream error of type H3_MESSAGE_ERROR.
            throw new Http3StreamException(Http3ErrorCode.MessageError, exception.Message, exception);
        }

        // RFC 9110 §9.3.6 / RFC 9114 §4.4 — a CONNECT's DATA frames are tunnel octets, not a message body.
        bool isConnect = requestHead.Method == HttpMethod.Connect;
        Http3RequestBodyStream body = new(
            this,
            reader,
            streamConnection,
            requestStreamId,
            trailers,
            contentLength,
            isConnect,
            _limits.MaxRequestBodySize,
            _limits.MaxRequestHeadersFrameSize,
            headToken);
        requestHead = requestHead with { Body = body };

        HttpConnectionInfo connectionInfo = HttpTlsConnectionInfo.Create(streamConnection.LocalEndPoint, streamConnection.RemoteEndPoint, _tls);

        if (_requestInterceptors.Length > 0 || _responseInterceptors.Length > 0)
        {
            // Interceptor hooks are application code. The seam contract makes the parse-path hooks
            // CPU-only, but a hook that blocks anyway (one that reads the whole body before dispatch, say)
            // must stall only its own stream, never the accept loop, so the hooks run on a thread-pool
            // thread instead of inline on the loop.
            await Task.CompletedTask.ConfigureAwait(ConfigureAwaitOptions.ForceYielding);
        }

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
                _limits.MaxRequestBodySize,
                isConnect).ConfigureAwait(false);
        }
        catch (HttpRequestRejectedException)
        {
            // A request-parse interceptor refused this request before it was dispatched (the pipeline
            // already disposed the partial body-wrapper chain and hook-attached features). RFC 9114
            // §4.1.1 — the server refused it without application processing, so the stream is reset with
            // H3_REQUEST_REJECTED rather than answered with the h1-style status response. The QUIC
            // connection and its other streams are unaffected.
            ResetRequestStream(
                streamConnection,
                requestStreamId,
                new Http3StreamException(Http3ErrorCode.RequestRejected, "A request interceptor rejected the request."),
                abandonsReading: !reader.IsCompleted);
            return null;
        }
        catch (Http3LimitExceededException exception)
        {
            // A request hook read the body before dispatch and the body exceeded the cap. The request
            // never became an exchange, so the transport answers the rejection (413) itself and then
            // stops reading the request stream.
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
            interception.Features)
        {
            AddedResponseInterceptors = interception.ResponseInterceptors,
        };
        body.AttachOwner(context);

        AttachExtendedConnect(context, extendedConnectProtocol);

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
        IHttpExchangeInterceptor[] responseInterceptors = context.ResolveResponseInterceptors(_responseInterceptors);

        if (responseInterceptors.Length > 0)
        {
            context.RunResponseInterceptors(
                responseInterceptors,
                new Http3ResponseBodyStream(context),
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
                return QPackFieldSectionDecoder.Decode(fieldSection);
            }
            catch (Exception exception) when (IsFieldSectionDecodeFailure(exception))
            {
                throw new Http3StreamException(Http3ErrorCode.MessageError, "The HTTP/3 field section could not be decoded.", exception);
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
            throw new Http3StreamException(Http3ErrorCode.MessageError, "The HTTP/3 field section prefix could not be decoded.", exception);
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
                throw new Http3StreamException(Http3ErrorCode.MessageError, "The HTTP/3 field section could not be decoded.", exception);
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
        public StreamAcceptOutcome(IConnection? streamConnection, bool terminate)
        {
            StreamConnection = streamConnection;
            TerminateConnection = terminate;
        }

        public IConnection? StreamConnection { get; }
        public bool TerminateConnection { get; }
    }

    public override async ValueTask SendAsync(IHttpContext context, CancellationToken cancellationToken = default)
    {
        if (context is not Http3Context http3Context)
        {
            throw new InvalidOperationException("The supplied context does not belong to an HTTP/3 connection.");
        }

        Http3RequestBodyStream requestBody = http3Context.RequestBody;

        // The transport already reset this request stream — a stream error surfaced while the request
        // body was read — so there is no stream left to answer on.
        if (requestBody.IsReset)
        {
            return;
        }

        // An accepted extended CONNECT tunnel already sent the exchange's only head; end the tunnel
        // rather than write a response (RFC 9220).
        if (http3Context.Tunnel is { } tunnel)
        {
            await FinishTunnelAsync(http3Context, tunnel, cancellationToken).ConfigureAwait(false);
            return;
        }

        // The exchange was aborted (IHttpExchangeControl.Abort / IHttpContext.Cancel — the
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
            await requestBody.TryDrainAsync(requestBodyDrainBudget, _requestBodyDrainTimeout).ConfigureAwait(false);
            await sink.CompleteAsync(cancellationToken).ConfigureAwait(false);
            StopReadingRequestStream(requestBody, http3Context.StreamId);
            await http3Context.InvokeAfterResponseAsync(cancellationToken).ConfigureAwait(false);
            return;
        }

        // RFC 9110 §15.5.14 — the request body exceeded the body-size cap and the final response head
        // has not been committed, so the exchange is answered 413 Content Too Large, whatever the
        // application staged from a body it never fully received — unless it answered 413 itself, whose
        // representation is kept.
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
            await requestBody.TryDrainAsync(requestBodyDrainBudget, _requestBodyDrainTimeout).ConfigureAwait(false);
            await hookStartedSink.CompleteAsync(cancellationToken).ConfigureAwait(false);
            StopReadingRequestStream(requestBody, http3Context.StreamId);
            await http3Context.InvokeAfterResponseAsync(cancellationToken).ConfigureAwait(false);
            return;
        }

        // RFC 9110 §15.2 — a 1xx status is never a valid final response status. Interim responses go
        // through the IHttpExchangeControl interim writes; HTTP/3 has no 101 (RFC 9114 §4.2), so the
        // rejection is unconditional.
        HttpInterimResponseRules.EnsureFinalStatusCode(http3Context.Response.StatusCode);

        // Commit point: from here the final response is on the wire, so the exchange control's
        // probes must report the response as started (no more interim writes).
        http3Context.MarkFinalResponseStarted();

        Stream stream = http3Context.StreamConnection.AsStream();
        byte[] bodyBytes = await ReadBodyAsync(http3Context.Response.Body, cancellationToken).ConfigureAwait(false);

        // RFC 9110 §9.3.2 — a response to HEAD carries the header section a GET would but never
        // content: no DATA frame. A Content-Length the application set is preserved; one is synthesized
        // only from a body the handler actually produced (the GET representation's length). An empty
        // HEAD body gets none, because RFC 9110 §8.6 forbids a Content-Length that differs from what GET
        // would send, and the transport cannot know it — the choice the HTTP/2 path makes too.
        bool isHead = http3Context.Request.Method == HttpMethod.Head;
        byte[] headerBlock = isHead && bodyBytes.Length == 0
            ? Http3HeaderCodec.EncodeResponseHeaders(http3Context)
            : Http3HeaderCodec.EncodeResponseHeaders(http3Context, bodyBytes);

        await WriteFrameAsync(stream, Http3FrameType.Headers, headerBlock, cancellationToken).ConfigureAwait(false);

        if (bodyBytes.Length > 0 && !isHead)
        {
            await WriteFrameAsync(stream, Http3FrameType.Data, bodyBytes, cancellationToken).ConfigureAwait(false);
        }

        // RFC 9114 §4.1 — staged trailers follow the content as a HEADERS frame, before the FIN that
        // ends the response. A response to HEAD carries none, as on HTTP/2.
        if (!isHead && http3Context.Response.StagedTrailers is { } trailers)
        {
            await WriteFrameAsync(stream, Http3FrameType.Headers, Http3HeaderCodec.EncodeTrailers(trailers), cancellationToken).ConfigureAwait(false);
        }

        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);

        // Let a nearly finished upload complete before the response ends (see TryDrainAsync): on the QUIC
        // driver, ending the response releases the stream, and an unread request is then stopped.
        await requestBody.TryDrainAsync(requestBodyDrainBudget, _requestBodyDrainTimeout).ConfigureAwait(false);

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
            new Http3StreamException(Http3ErrorCode.RequestCancelled, "The exchange was cancelled."),
            abandonsReading);
    }

    /// <summary>
    /// Stops reading a request stream once its complete response is on the wire (RFC 9114 §4.1): an
    /// unread remainder of the request is refused with <c>STOP_SENDING(H3_NO_ERROR)</c>. With the dynamic
    /// table enabled, abandoning the stream before its end also emits a Stream Cancellation (RFC 9204
    /// §4.4.2), since a trailer section left unread will never be acknowledged.
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
        IConnection streamConnection,
        Http3RequestBodyStream requestBody,
        long requestStreamId,
        HttpStatusCode statusCode,
        CancellationToken cancellationToken)
    {
        try
        {
            Stream stream = streamConnection.AsStream();
            byte[] headerBlock = Http3HeaderCodec.EncodeStatusOnlyResponseHeaders(statusCode);

            await WriteFrameAsync(stream, Http3FrameType.Headers, headerBlock, cancellationToken).ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            await requestBody.TryDrainAsync(requestBodyDrainBudget, _requestBodyDrainTimeout).ConfigureAwait(false);
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
            new Http3StreamException(Http3ErrorCode.RequestRejected, "The connection stopped before the request was dispatched."),
            abandonsReading);

        await context.DisposeAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// Ends the request stream's write side once the final response is fully written, emitting the
    /// graceful QUIC FIN that delimits an HTTP/3 response body (RFC 9114 §4.1). Completing the
    /// connection's outbound <see cref="PipeWriter"/> is the <see cref="IConnection"/> contract's
    /// documented half-close signal. Best-effort: the response bytes are already flushed to the
    /// transport, so a teardown race that disposes the stream underneath the completion is benign.
    /// </summary>
    /// <param name="streamConnection">The request stream whose write side is ended.</param>
    private static void CompleteResponseStreamWrites(IConnection streamConnection)
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
    internal async Task WriteInterimResponseAsync(
        Http3Context http3Context,
        HttpStatusCode statusCode,
        IHttpHeaderCollection? headers,
        CancellationToken cancellationToken)
    {
        Stream stream = http3Context.StreamConnection.AsStream();
        byte[] headerBlock = Http3HeaderCodec.EncodeInterimResponseHeaders(statusCode, headers);

        await WriteFrameAsync(stream, Http3FrameType.Headers, headerBlock, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
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
