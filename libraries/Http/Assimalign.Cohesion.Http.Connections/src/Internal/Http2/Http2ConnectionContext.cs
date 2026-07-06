using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

using Assimalign.Cohesion.Connections;
using Assimalign.Cohesion.Http.Connections.Internal.Http2.HPack;

namespace Assimalign.Cohesion.Http.Connections.Internal.Http2;

internal sealed class Http2ConnectionContext : HttpStreamConnectionContext, IAsyncDisposable, IHttp2RequestBodySink
{
    private static readonly byte[] ClientPreface = Encoding.ASCII.GetBytes("PRI * HTTP/2.0\r\n\r\nSM\r\n\r\n");

    private readonly HPackDecoder _headerDecoder;
    private readonly Dictionary<int, Http2Stream> _streams;
    // Guards the shared mutable state the frame pump and the application threads
    // both touch: the stream table and the *receive*-direction flow-control
    // windows (the pump consumes them as DATA arrives; the request body streams
    // credit them back as the application consumes). Held only for short,
    // synchronous critical sections — never across an await or a wire read.
    private readonly object _syncRoot = new();
    // RFC 9113 §6.5 — local settings are what we (the server) advertised
    // to the peer; remote settings are what the peer advertised to us.
    // Inbound frame parsing is bounded by the local cap; outbound frame
    // writing is bounded by the remote cap.
    private readonly Http2ConnectionSettings _localSettings;
    private readonly Http2ConnectionSettings _remoteSettings;
    // RFC 9113 §4.1 + §6.8 — a single TCP connection multiplexes many
    // logical streams. Frames from concurrent senders MUST NOT interleave
    // on the wire, or the peer's parser will see a corrupted frame
    // sequence. This semaphore serializes every outbound frame write on
    // the connection, regardless of whether it originated from a
    // SendAsync, a SETTINGS/PING ACK, a WINDOW_UPDATE, a GOAWAY, or
    // graceful shutdown.
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    // Ready request heads produced by the frame pump and consumed by ReceiveAsync.
    // The pump dispatches a context as soon as a stream's header block is complete
    // (RFC 9113 lets the server respond before the body arrives), then keeps
    // pumping DATA into that stream's body pipe while the application handles it.
    private readonly Channel<Http2Context> _readyContexts;
    // RFC 9113 §5.2 — every HTTP/2 endpoint maintains two connection-level
    // flow-control windows (independent of the per-stream windows). The
    // send window caps outbound DATA across all streams combined; the
    // receive window caps inbound DATA likewise. Both default to 65535
    // octets per RFC 9113 §6.5.2 and are NOT affected by
    // SETTINGS_INITIAL_WINDOW_SIZE. The receive window is only replenished
    // as the application consumes buffered request bodies (see
    // OnRequestBodyConsumedAsync), which is what applies real backpressure.
    private Http2FlowControlWindow _connectionSendWindow = new(Http2ConnectionSettings.InitialInitialWindowSize);
    private Http2FlowControlWindow _connectionReceiveWindow = new(Http2ConnectionSettings.InitialInitialWindowSize);
    private CancellationTokenSource? _pumpCts;
    private Task? _pumpTask;
    private int _pumpStarted;
    private int? _continuationStreamId;
    private bool _initialized;
    private bool _receivedClientSettings;
    private int _lastInboundStreamId;
    private int _gracefulCloseStarted;
    // RFC 9113 §6.8 — after the peer sends GOAWAY, the server MUST NOT
    // process new streams with id higher than the GOAWAY's last-stream-id.
    // -1 means no GOAWAY received yet; int.MaxValue would mean "accept
    // everything" (also "no GOAWAY"). We use null-int via sentinel to
    // keep the field a plain int.
    private int _peerLastStreamId = -1;

    public Http2ConnectionContext(IConnection connection, bool isSecure)
        : base(connection, isSecure)
    {
        _headerDecoder = new HPackDecoder();
        _streams = new Dictionary<int, Http2Stream>();
        _localSettings = BuildLocalSettings();
        _remoteSettings = new Http2ConnectionSettings();
        _readyContexts = Channel.CreateUnbounded<Http2Context>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = true,
            AllowSynchronousContinuations = false,
        });
    }

    /// <summary>
    /// Yields HTTP/2 request contexts for the lifetime of this connection.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A dedicated background frame pump reads and processes every inbound frame,
    /// decoupled from the pace at which the consumer handles requests. This is what
    /// makes request-body flow control work end-to-end: the pump keeps draining the
    /// wire (feeding per-stream body pipes and enforcing the receive window) while a
    /// request handler runs, so a slow reader applies backpressure to the sender
    /// instead of blocking the whole connection.
    /// </para>
    /// <para>
    /// Wire-level failures and HTTP/2 connection-level protocol errors scope to this
    /// connection only. <see cref="Http2ConnectionException"/> emits the required
    /// <c>GOAWAY</c> (RFC 9113 §6.8) and then completes the enumerable cleanly; the
    /// listener stays alive for subsequent peers. <see cref="Http2StreamException"/>
    /// stays recoverable per RFC 9113 §5.4.2 &#8211; a <c>RST_STREAM</c> is emitted
    /// and the pump continues processing other streams on the same connection.
    /// Cancellation propagates so cooperative shutdown is unaffected.
    /// </para>
    /// </remarks>
    public override async IAsyncEnumerable<IHttpContext> ReceiveAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        StartPump(cancellationToken);

        ChannelReader<Http2Context> reader = _readyContexts.Reader;
        while (await reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
        {
            while (reader.TryRead(out Http2Context? context))
            {
                yield return context;
            }
        }
    }

    /// <summary>
    /// Starts the background frame pump exactly once. The pump owns all inbound
    /// frame processing for the connection's lifetime; its cancellation is linked
    /// to the enumeration token (cooperative shutdown) and to
    /// <see cref="GracefulCloseAsync"/> / disposal.
    /// </summary>
    private void StartPump(CancellationToken cancellationToken)
    {
        if (Interlocked.Exchange(ref _pumpStarted, 1) == 1)
        {
            return;
        }

        _pumpCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _pumpTask = Task.Run(() => PumpAsync(_pumpCts.Token));
    }

    /// <summary>
    /// The connection's single inbound frame processor. Reads frames, applies
    /// protocol rules, dispatches ready request heads to <see cref="_readyContexts"/>,
    /// and feeds DATA into per-stream body pipes — all decoupled from how fast the
    /// consumer handles requests, which is what lets request-body flow control apply
    /// real backpressure.
    /// </summary>
    private async Task PumpAsync(CancellationToken cancellationToken)
    {
        try
        {
            if (!await TryInitializeAsync(cancellationToken).ConfigureAwait(false))
            {
                return;
            }

            while (!cancellationToken.IsCancellationRequested)
            {
                FrameReadOutcome read = await TryReadFrameAsync(cancellationToken).ConfigureAwait(false);
                if (read.TerminateConnection || read.Frame is null)
                {
                    return;
                }

                FrameProcessOutcome processed = await TryProcessFrameAsync(read.Frame.Value, cancellationToken).ConfigureAwait(false);
                if (processed.TerminateConnection)
                {
                    return;
                }

                if (processed.Context is not null)
                {
                    _readyContexts.Writer.TryWrite(processed.Context);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Cooperative shutdown — the enumeration token or graceful close fired.
        }
        catch (Exception)
        {
            // Last-resort isolation: an unexpected fault in the pump tears down only
            // this connection, never the listener. The finally still unblocks the
            // consumer and any in-flight body readers.
        }
        finally
        {
            // Abort in-flight body readers BEFORE signalling the consumer that no
            // more requests are coming, so a handler observes the abort rather than
            // racing a clean end-of-enumerable.
            AbortPendingRequests();
            _readyContexts.Writer.TryComplete();
        }
    }

    /// <summary>
    /// Aborts every live stream when the pump stops (teardown, wire failure, or
    /// connection error): fires each stream's <c>RequestAborted</c> and completes
    /// its body pipe, so a request handler parked reading a still-incoming body
    /// observes cancellation rather than a clean end-of-stream (which would let it
    /// treat a truncated body as complete) — and never hangs.
    /// </summary>
    private void AbortPendingRequests()
    {
        Http2Stream[] streams;
        lock (_syncRoot)
        {
            streams = new Http2Stream[_streams.Count];
            _streams.Values.CopyTo(streams, 0);
        }

        foreach (Http2Stream stream in streams)
        {
            stream.AbortOnShutdown();
        }
    }

    /// <summary>
    /// Drives <see cref="EnsureInitializedAsync"/> with the same per-connection
    /// failure-isolation contract as the pump loop: any preface mismatch (which
    /// already emits GOAWAY before throwing), wire-level read failure during the
    /// preface, or write failure while emitting the server SETTINGS terminates
    /// the pump cleanly rather than crashing the listener.
    /// </summary>
    private async Task<bool> TryInitializeAsync(CancellationToken cancellationToken)
    {
        try
        {
            await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (Http2ConnectionException)
        {
            // EnsureInitializedAsync already emitted GOAWAY before
            // throwing on a preface mismatch.
            return false;
        }
        catch (Exception ex) when (IsWireLevelFailure(ex))
        {
            return false;
        }
    }

    /// <summary>
    /// Reads the next HTTP/2 frame, classifying failures so the pump loop
    /// can route them: connection-level protocol errors emit
    /// <c>GOAWAY</c> and terminate; wire-level transport failures
    /// terminate silently (no GOAWAY is possible after the peer is
    /// already gone); cancellation propagates.
    /// </summary>
    private async Task<FrameReadOutcome> TryReadFrameAsync(CancellationToken cancellationToken)
    {
        try
        {
            ReceivedFrame? frame = await ReadFrameAsync(Stream, _localSettings.MaxFrameSize, cancellationToken).ConfigureAwait(false);
            return new FrameReadOutcome(frame, terminate: false);
        }
        catch (Http2ConnectionException error)
        {
            await TryEmitGoAwayAsync(error.ErrorCode, cancellationToken).ConfigureAwait(false);
            return new FrameReadOutcome(frame: null, terminate: true);
        }
        catch (Exception ex) when (IsWireLevelFailure(ex))
        {
            return new FrameReadOutcome(frame: null, terminate: true);
        }
    }

    /// <summary>
    /// Processes a single received frame, classifying failures:
    /// stream-level errors emit <c>RST_STREAM</c> and let the loop
    /// continue (RFC 9113 §5.4.2 — stream errors are recoverable);
    /// connection-level errors emit <c>GOAWAY</c> and terminate the
    /// connection (RFC 9113 §6.8); wire-level transport failures
    /// terminate without GOAWAY.
    /// </summary>
    private async Task<FrameProcessOutcome> TryProcessFrameAsync(ReceivedFrame received, CancellationToken cancellationToken)
    {
        try
        {
            Http2Context? context = await ProcessFrameAsync(received, cancellationToken).ConfigureAwait(false);
            return new FrameProcessOutcome(context, terminate: false);
        }
        catch (Http2StreamException streamError)
        {
            await TryEmitRstStreamAsync(streamError.StreamId, streamError.ErrorCode, cancellationToken).ConfigureAwait(false);
            return new FrameProcessOutcome(context: null, terminate: false);
        }
        catch (Http2ConnectionException error)
        {
            await TryEmitGoAwayAsync(error.ErrorCode, cancellationToken).ConfigureAwait(false);
            return new FrameProcessOutcome(context: null, terminate: true);
        }
        catch (Exception ex) when (IsWireLevelFailure(ex))
        {
            return new FrameProcessOutcome(context: null, terminate: true);
        }
    }

    private async Task TryEmitGoAwayAsync(Http2ErrorCode errorCode, CancellationToken cancellationToken)
    {
        try
        {
            await EmitGoAwayAsync(errorCode, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            // The peer may already be gone — the GOAWAY is best-effort
            // since the connection is terminating regardless.
        }
    }

    private async Task TryEmitRstStreamAsync(int streamId, Http2ErrorCode errorCode, CancellationToken cancellationToken)
    {
        try
        {
            await EmitRstStreamAsync(streamId, errorCode, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            // Best-effort: a transport failure while emitting RST_STREAM
            // will surface as a connection-level read failure on the next
            // iteration and tear the connection down through the normal
            // path.
        }
    }

    private static bool IsWireLevelFailure(Exception exception)
    {
        return exception is EndOfStreamException
            or IOException
            or System.Net.Sockets.SocketException;
    }

    private readonly struct FrameReadOutcome
    {
        public FrameReadOutcome(ReceivedFrame? frame, bool terminate)
        {
            Frame = frame;
            TerminateConnection = terminate;
        }

        public ReceivedFrame? Frame { get; }
        public bool TerminateConnection { get; }
    }

    private readonly struct FrameProcessOutcome
    {
        public FrameProcessOutcome(Http2Context? context, bool terminate)
        {
            Context = context;
            TerminateConnection = terminate;
        }

        public Http2Context? Context { get; }
        public bool TerminateConnection { get; }
    }

    public override async ValueTask SendAsync(IHttpContext context, CancellationToken cancellationToken = default)
    {
        if (context is not Http2Context http2Context)
        {
            throw new InvalidOperationException("The supplied context does not belong to an HTTP/2 connection.");
        }

        // RFC 9113 §8.1 — the application cancelled this exchange (via
        // IHttpContext.Cancel, e.g. a rejected extended CONNECT). Reset the
        // stream with CANCEL instead of writing a response; the connection and
        // its other streams are unaffected. EmitRstStreamAsync takes the write
        // lock itself, so this runs before we acquire it below.
        if (http2Context.CancelRequested)
        {
            await EmitRstStreamAsync(http2Context.StreamId, Http2ErrorCode.Cancel, cancellationToken).ConfigureAwait(false);
            return;
        }

        byte[] bodyBytes = await ReadBodyAsync(http2Context.Response.Body, cancellationToken).ConfigureAwait(false);
        byte[] headerBlock = HPackEncoder.EncodeResponseHeaders(http2Context.Response.StatusCode, http2Context.Response.Headers, bodyBytes.Length);

        // RFC 9113 §4.1 — frames from concurrent senders MUST NOT interleave.
        // The whole HEADERS [+ CONTINUATION...] [+ DATA...] sequence MUST be
        // emitted contiguously on the wire. Hold the connection write lock
        // for the duration so concurrent SendAsync calls, pump ACK/WINDOW_UPDATE
        // frames, and body-consumption WINDOW_UPDATEs cannot tear the sequence.
        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await WriteHeaderBlockAsync(http2Context.StreamId, headerBlock, endStream: bodyBytes.Length == 0, cancellationToken).ConfigureAwait(false);
            await WriteBodyAsync(http2Context.StreamId, bodyBytes, cancellationToken).ConfigureAwait(false);
            // RFC 9113 §6.8 / #686 — the caller MUST observe a guarantee that
            // the response bytes have been handed off to the transport pipe
            // before SendAsync returns. Flush the pipe writer here so the
            // bytes are durably committed to the send-side of the underlying
            // transport before the lock is released.
            await Stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }

        // RFC 9113 §5.1 — every server response ends with END_STREAM (either
        // on a body-less HEADERS frame or on the trailing DATA frame). After
        // a successful send the stream's local half is closed; if the peer
        // already closed its half (the normal request/response case) the
        // stream transitions to Closed.
        http2Context.Stream.SendEndStream();

        if (http2Context.Stream.IsClosed)
        {
            // Cleanup: once the stream is fully Closed (both halves done), drop
            // it from the active map so the per-connection memory footprint
            // stays bounded over many requests. Reclaims the connection-window
            // debt of a body the handler never drained.
            await RemoveStreamAsync(http2Context.StreamId, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            // The server responded before the peer finished sending the request
            // body (the handler did not drain it). RFC 9113 §8.1 — reset the
            // stream with NO_ERROR to tell the peer to stop sending and to
            // reclaim the stream slot against SETTINGS_MAX_CONCURRENT_STREAMS,
            // rather than leaving a HalfClosedLocal stream lingering forever.
            await EmitRstStreamAsync(http2Context.StreamId, Http2ErrorCode.NoError, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    /// <remarks>
    /// RFC 9113 §5.2 / §6.9 — request-body flow control is receiver-driven and
    /// consumption-paced. As the application drains a buffered <see cref="Http2DataChunk"/>,
    /// its full flow-control cost is credited back to both the stream and the
    /// connection receive windows and the paired <c>WINDOW_UPDATE</c> frames are
    /// emitted, which is what resumes a sender that stalled because the reader was
    /// slow.
    /// </remarks>
    public async ValueTask OnRequestBodyConsumedAsync(int streamId, int flowControlLength, CancellationToken cancellationToken)
    {
        if (flowControlLength <= 0)
        {
            return;
        }

        lock (_syncRoot)
        {
            // If the stream is gone or its receive debt was already reclaimed to
            // the connection window on removal, this credit would double-count —
            // skip it. Otherwise credit both windows atomically.
            if (!_streams.TryGetValue(streamId, out Http2Stream? stream) || stream.ReceiveReclaimed)
            {
                return;
            }

            stream.ReceiveWindow.TryReplenish(flowControlLength);
            _connectionReceiveWindow.TryReplenish(flowControlLength);
        }

        // Connection-level credit first, then the stream-level credit.
        await EmitWindowUpdateAsync(0, flowControlLength, cancellationToken).ConfigureAwait(false);
        await EmitWindowUpdateAsync(streamId, flowControlLength, cancellationToken).ConfigureAwait(false);
    }

    private async Task EnsureInitializedAsync(CancellationToken cancellationToken)
    {
        if (_initialized)
        {
            return;
        }

        // RFC 9113 §3.4 — the connection preface MUST appear before any
        // other client data. A mismatch is a connection error with code
        // PROTOCOL_ERROR.
        byte[] preface = await ReadExactOrThrowAsync(Stream, ClientPreface.Length, cancellationToken).ConfigureAwait(false);

        if (!preface.AsSpan().SequenceEqual(ClientPreface))
        {
            await EmitGoAwayAsync(Http2ErrorCode.ProtocolError, cancellationToken).ConfigureAwait(false);
            throw new Http2ConnectionException(
                Http2ErrorCode.ProtocolError,
                "The HTTP/2 connection preface did not match the expected sequence.");
        }

        // RFC 9113 §3.4 + §6.5 — immediately after sending or receiving
        // the preface, both peers send a SETTINGS frame (possibly empty)
        // as the first connection frame. We advertise the parameters
        // built from our local defaults instead of an empty payload so
        // the peer sees what we actually want.
        byte[] settingsPayload = EncodeLocalSettings();
        Http2Frame settingsFrame = new();
        settingsFrame.PrepareSettings(Http2SettingsFrameFlags.None);
        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await Http2FrameWriter.WriteAsync(Stream, settingsFrame, settingsPayload, cancellationToken).ConfigureAwait(false);
            await Stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }

        _initialized = true;
    }

    private async Task<Http2Context?> ProcessFrameAsync(ReceivedFrame receivedFrame, CancellationToken cancellationToken)
    {
        // RFC 9113 §3.4 — the first frame the client sends after the
        // preface MUST be SETTINGS. Anything else is a PROTOCOL_ERROR.
        if (!_receivedClientSettings && receivedFrame.Frame.Type != Http2FrameType.Settings)
        {
            throw new Http2ConnectionException(
                Http2ErrorCode.ProtocolError,
                $"Expected the HTTP/2 client SETTINGS frame as the first frame after the preface, but received '{receivedFrame.Frame.Type}'.");
        }

        if (_continuationStreamId.HasValue && receivedFrame.Frame.Type != Http2FrameType.Continuation)
        {
            throw new Http2ConnectionException(
                Http2ErrorCode.ProtocolError,
                "Expected an HTTP/2 CONTINUATION frame before receiving another frame type.");
        }

        switch (receivedFrame.Frame.Type)
        {
            case Http2FrameType.Settings:
                await ProcessSettingsFrameAsync(receivedFrame, cancellationToken).ConfigureAwait(false);
                return null;
            case Http2FrameType.Headers:
                return ProcessHeadersFrame(receivedFrame);
            case Http2FrameType.Continuation:
                return ProcessContinuationFrame(receivedFrame);
            case Http2FrameType.Data:
                return await ProcessDataFrameAsync(receivedFrame, cancellationToken).ConfigureAwait(false);
            case Http2FrameType.Ping:
                await ProcessPingFrameAsync(receivedFrame, cancellationToken).ConfigureAwait(false);
                return null;
            case Http2FrameType.RstStream:
                await ProcessRstStreamFrameAsync(receivedFrame, cancellationToken).ConfigureAwait(false);
                return null;
            case Http2FrameType.WindowUpdate:
                ProcessWindowUpdateFrame(receivedFrame);
                return null;
            case Http2FrameType.GoAway:
                ProcessGoAwayFrame(receivedFrame);
                return null;
            case Http2FrameType.PushPromise:
                // RFC 9113 §8.4 / §6.6 — only servers send PUSH_PROMISE, and a
                // client cannot push. A server MUST treat the receipt of a
                // PUSH_PROMISE frame as a connection error of type
                // PROTOCOL_ERROR. Cohesion does not implement server push
                // (de-scoped; see docs/DESIGN.md), so rejecting it here is both
                // the RFC requirement and the enforcement of that decision —
                // without this case the frame would fall through and be
                // silently ignored.
                throw new Http2ConnectionException(
                    Http2ErrorCode.ProtocolError,
                    "PUSH_PROMISE received from the client; HTTP/2 servers do not accept pushed streams (RFC 9113 §8.4).");
            default:
                // RFC 9113 §5.1 — frame types the endpoint does not recognise
                // MUST be ignored. PRIORITY (0x2, deprecated) and any unknown
                // extension frame land here.
                return null;
        }
    }

    private void ProcessGoAwayFrame(ReceivedFrame receivedFrame)
    {
        // RFC 9113 §6.8 — GOAWAY frames MUST be on stream 0 and carry at
        // least 8 octets of payload (4-byte last-stream-id + 4-byte error
        // code; additional debug data is permitted but ignored here).
        if (receivedFrame.Frame.StreamId != 0)
        {
            throw new Http2ConnectionException(
                Http2ErrorCode.ProtocolError,
                $"HTTP/2 GOAWAY received on stream {receivedFrame.Frame.StreamId}; GOAWAY MUST use stream 0.");
        }

        // The frame reader has already parsed last-stream-id + error code
        // into the frame's GoAway* properties (see Http2FrameReader and
        // Http2Frame.GoAway). RFC §6.8 says last-stream-id may decrease
        // across multiple GOAWAYs only for tighter shutdowns, never
        // expand. We accept whatever the peer sent without re-validating
        // because we treat GOAWAY as a unidirectional close signal — the
        // peer is telling us "don't open new streams beyond X."
        _peerLastStreamId = receivedFrame.Frame.GoAwayLastStreamId;
    }

    private void ProcessWindowUpdateFrame(ReceivedFrame receivedFrame)
    {
        // RFC 9113 §6.9 — WINDOW_UPDATE has a fixed 4-octet payload (the
        // 31-bit increment plus a reserved bit). Anything else is a
        // FRAME_SIZE_ERROR connection-level fault. We inspect the frame's
        // declared total payload length (not `receivedFrame.Payload`,
        // which strips the parsed extended header and is empty for a
        // valid WINDOW_UPDATE).
        if (receivedFrame.Frame.PayloadLength != 4)
        {
            throw new Http2ConnectionException(
                Http2ErrorCode.FrameSizeError,
                $"HTTP/2 WINDOW_UPDATE frame payload must be 4 octets; got {receivedFrame.Frame.PayloadLength}.");
        }

        int increment = receivedFrame.Frame.WindowUpdateSizeIncrement;

        // RFC 9113 §6.9 — an increment of 0 is a protocol error:
        // connection-level when delivered on stream 0, stream-level
        // when delivered on a specific stream.
        if (increment == 0)
        {
            if (receivedFrame.Frame.StreamId == 0)
            {
                throw new Http2ConnectionException(
                    Http2ErrorCode.ProtocolError,
                    "HTTP/2 WINDOW_UPDATE on stream 0 with increment 0.");
            }

            throw new Http2StreamException(
                receivedFrame.Frame.StreamId,
                Http2ErrorCode.ProtocolError,
                $"HTTP/2 WINDOW_UPDATE on stream {receivedFrame.Frame.StreamId} with increment 0.");
        }

        if (receivedFrame.Frame.StreamId == 0)
        {
            // Connection-level credit. Overflow → FLOW_CONTROL_ERROR
            // connection-level (RFC 9113 §6.9.1). The send window is
            // pump-owned, but take the lock for uniformity with the
            // receive-side accounting.
            lock (_syncRoot)
            {
                if (!_connectionSendWindow.TryReplenish(increment))
                {
                    throw new Http2ConnectionException(
                        Http2ErrorCode.FlowControlError,
                        $"HTTP/2 connection-level send window would exceed {Http2FlowControlWindow.MaxValue} after WINDOW_UPDATE.");
                }
            }

            return;
        }

        // Stream-level credit. Overflow → FLOW_CONTROL_ERROR
        // stream-level (RFC 9113 §6.9.1). If the stream is unknown we
        // disambiguate the same way as DATA / RST_STREAM: id > highest
        // observed is "idle" (connection error); id ≤ highest is
        // "recently closed" (silently ignore — the peer is racing our
        // RST_STREAM or our END_STREAM acknowledgement, RFC 9113 §5.1).
        lock (_syncRoot)
        {
            if (!_streams.TryGetValue(receivedFrame.Frame.StreamId, out Http2Stream? stream))
            {
                if (receivedFrame.Frame.StreamId > _lastInboundStreamId)
                {
                    throw new Http2ConnectionException(
                        Http2ErrorCode.ProtocolError,
                        $"HTTP/2 WINDOW_UPDATE on idle stream {receivedFrame.Frame.StreamId}.");
                }

                return;
            }

            if (!stream.SendWindow.TryReplenish(increment))
            {
                throw new Http2StreamException(
                    receivedFrame.Frame.StreamId,
                    Http2ErrorCode.FlowControlError,
                    $"HTTP/2 stream {receivedFrame.Frame.StreamId} send window would exceed {Http2FlowControlWindow.MaxValue} after WINDOW_UPDATE.");
            }
        }
    }

    private async Task ProcessRstStreamFrameAsync(ReceivedFrame receivedFrame, CancellationToken cancellationToken)
    {
        // RFC 9113 §6.4 — RST_STREAM frames MUST be associated with a
        // specific stream (stream 0 is illegal) and MUST carry a 4-octet
        // error code payload.
        if (receivedFrame.Frame.StreamId == 0)
        {
            throw new Http2ConnectionException(
                Http2ErrorCode.ProtocolError,
                "HTTP/2 RST_STREAM frame received on stream 0.");
        }

        // Remove-and-reclaim: the peer is abandoning the stream, so its buffered
        // but unconsumed body is discarded and its connection-window debt is
        // credited back (RFC 9113 §6.9).
        Http2Stream? stream = await RemoveStreamAsync(receivedFrame.Frame.StreamId, cancellationToken).ConfigureAwait(false);

        if (stream is not null)
        {
            stream.ReceiveReset();
        }
        else if (receivedFrame.Frame.StreamId > _lastInboundStreamId)
        {
            // RST_STREAM on an idle stream (one whose ID we have never
            // observed an in-bound frame for) is a connection error per
            // RFC 9113 §6.4.
            throw new Http2ConnectionException(
                Http2ErrorCode.ProtocolError,
                $"HTTP/2 RST_STREAM received for idle stream {receivedFrame.Frame.StreamId}.");
        }

        if (_continuationStreamId == receivedFrame.Frame.StreamId)
        {
            _continuationStreamId = null;
        }
    }

    /// <summary>
    /// Validates a client-initiated stream identifier per RFC 9113 §5.1.1:
    /// MUST be odd, MUST be non-zero, MUST be greater than every previously
    /// observed client-initiated stream identifier. Violations are
    /// connection-level PROTOCOL_ERROR. Must be called while holding
    /// <see cref="_syncRoot"/> because it inspects the stream table.
    /// </summary>
    private void ValidateInboundStreamId(int streamId)
    {
        if (streamId == 0)
        {
            throw new Http2ConnectionException(
                Http2ErrorCode.ProtocolError,
                "HTTP/2 stream-bearing frame received on stream 0.");
        }

        if ((streamId & 1) == 0)
        {
            throw new Http2ConnectionException(
                Http2ErrorCode.ProtocolError,
                $"HTTP/2 client-initiated stream id {streamId} must be odd.");
        }

        // Re-using a stream id is legal if the stream is still tracked
        // (continuation of HEADERS, body DATA, etc.). It is only an
        // ordering violation when opening a NEW stream with a smaller id
        // than the previously highest one.
        if (streamId < _lastInboundStreamId && !_streams.ContainsKey(streamId))
        {
            throw new Http2ConnectionException(
                Http2ErrorCode.ProtocolError,
                $"HTTP/2 stream id {streamId} is lower than the highest previously seen client-initiated stream id {_lastInboundStreamId}.");
        }
    }

    private async Task ProcessSettingsFrameAsync(ReceivedFrame receivedFrame, CancellationToken cancellationToken)
    {
        // RFC 9113 §6.5.1 — SETTINGS frames MUST be sent on stream 0.
        if (receivedFrame.Frame.StreamId != 0)
        {
            throw new Http2ConnectionException(
                Http2ErrorCode.ProtocolError,
                $"SETTINGS frame received on stream {receivedFrame.Frame.StreamId}; SETTINGS frames MUST use stream 0.");
        }

        if (receivedFrame.Frame.SettingsAck)
        {
            // RFC 9113 §6.5 — an ACK frame MUST have an empty payload. A
            // non-empty payload is a connection error with code
            // FRAME_SIZE_ERROR.
            if (receivedFrame.Payload.Length != 0)
            {
                throw new Http2ConnectionException(
                    Http2ErrorCode.FrameSizeError,
                    $"SETTINGS ACK frame must have an empty payload, but received {receivedFrame.Payload.Length} octets.");
            }

            return;
        }

        // RFC 9113 §6.5.1 — a non-ACK SETTINGS frame's payload must be a
        // multiple of 6 octets (each setting is 16-bit identifier + 32-bit
        // value). A non-multiple is a FRAME_SIZE_ERROR.
        if (receivedFrame.Payload.Length % Http2FrameReader.SettingSize != 0)
        {
            throw new Http2ConnectionException(
                Http2ErrorCode.FrameSizeError,
                $"SETTINGS frame payload of {receivedFrame.Payload.Length} octets is not a multiple of {Http2FrameReader.SettingSize}.");
        }

        // Validate every parameter before applying any. RFC 9113 §6.5.2
        // — an invalid value is a connection error with the per-parameter
        // code listed in the spec (PROTOCOL_ERROR or FLOW_CONTROL_ERROR).
        System.Collections.Generic.IList<Http2PeerSetting> settings =
            Http2FrameReader.ReadSettings(new ReadOnlySequence<byte>(receivedFrame.Payload));

        foreach (Http2PeerSetting setting in settings)
        {
            (Http2ErrorCode errorCode, string? message) = Http2ConnectionSettings.Validate(setting);
            if (errorCode != Http2ErrorCode.NoError)
            {
                throw new Http2ConnectionException(errorCode, message ?? $"Invalid HTTP/2 SETTINGS value for {setting.Parameter}.");
            }
        }

        // Snapshot the prior INITIAL_WINDOW_SIZE so we can compute the
        // delta to retroactively apply to existing streams' send windows
        // (RFC 9113 §6.9.2). Connection-level windows are NOT adjusted —
        // they have a fixed initial value of 65535 and only change via
        // WINDOW_UPDATE.
        uint priorInitialWindowSize = _remoteSettings.InitialWindowSize;

        foreach (Http2PeerSetting setting in settings)
        {
            _remoteSettings.Apply(setting);
        }

        // RFC 9113 §6.9.2 — when the peer changes SETTINGS_INITIAL_WINDOW_SIZE
        // we adjust every existing stream's send window by the delta. The
        // adjustment can drive the window negative; only when it would
        // exceed 2^31-1 is it a FLOW_CONTROL_ERROR.
        if (_remoteSettings.InitialWindowSize != priorInitialWindowSize)
        {
            long delta = (long)_remoteSettings.InitialWindowSize - priorInitialWindowSize;
            if (delta < int.MinValue || delta > int.MaxValue)
            {
                throw new Http2ConnectionException(
                    Http2ErrorCode.FlowControlError,
                    $"SETTINGS_INITIAL_WINDOW_SIZE delta {delta} is outside the legal range.");
            }

            int signedDelta = (int)delta;
            Http2Stream[] existingStreams;
            lock (_syncRoot)
            {
                existingStreams = new Http2Stream[_streams.Count];
                _streams.Values.CopyTo(existingStreams, 0);
            }

            foreach (Http2Stream existingStream in existingStreams)
            {
                if (!existingStream.SendWindow.TryAdjustInitialWindow(signedDelta))
                {
                    throw new Http2ConnectionException(
                        Http2ErrorCode.FlowControlError,
                        $"SETTINGS_INITIAL_WINDOW_SIZE change pushed stream {existingStream.StreamId} send window above {Http2FlowControlWindow.MaxValue}.");
                }
            }
        }

        _receivedClientSettings = true;

        Http2Frame acknowledgement = new();
        acknowledgement.PrepareSettings(Http2SettingsFrameFlags.Acknowledge);
        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await Http2FrameWriter.WriteAsync(Stream, acknowledgement, ReadOnlyMemory<byte>.Empty, cancellationToken).ConfigureAwait(false);
            await Stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private Http2Context? ProcessHeadersFrame(ReceivedFrame receivedFrame)
    {
        if (!_receivedClientSettings)
        {
            throw new Http2ConnectionException(
                Http2ErrorCode.ProtocolError,
                "The HTTP/2 client SETTINGS frame must be received before HEADERS.");
        }

        Http2Frame frame = receivedFrame.Frame;

        Http2Stream stream = OpenInboundStream(frame);

        // Hand the HEADERS frame to the state machine — it folds the payload
        // into the accumulating header block AND updates the stream's
        // lifecycle state (idle → open or idle → half-closed-remote).
        stream.ReceiveHeaders(receivedFrame.Payload, frame.HeadersEndHeaders, frame.HeadersEndStream);

        if (!frame.HeadersEndHeaders)
        {
            _continuationStreamId = frame.StreamId;
            return null;
        }

        return TryDispatchStream(stream);
    }

    /// <summary>
    /// Validates, admission-controls, and materializes the inbound stream a HEADERS
    /// frame targets, under the stream-table lock.
    /// </summary>
    private Http2Stream OpenInboundStream(Http2Frame frame)
    {
        lock (_syncRoot)
        {
            // RFC 9113 §5.1.1 — client-initiated streams MUST use odd-numbered
            // identifiers, and stream IDs MUST increase monotonically. The server
            // never initiates a stream (PUSH_PROMISE is disabled), so we treat any
            // even ID as a connection-level PROTOCOL_ERROR.
            ValidateInboundStreamId(frame.StreamId);

            bool openingNewStream = !_streams.ContainsKey(frame.StreamId);
            if (openingNewStream)
            {
                // RFC 9113 §6.8 — after we've initiated a graceful close, refuse
                // new streams with RST_STREAM(REFUSED_STREAM). The peer can
                // safely retry these on a fresh connection (RFC §8.1.4).
                if (Volatile.Read(ref _gracefulCloseStarted) == 1)
                {
                    throw new Http2StreamException(
                        frame.StreamId,
                        Http2ErrorCode.RefusedStream,
                        $"HTTP/2 stream {frame.StreamId} refused: server is closing the connection.");
                }

                // RFC 9113 §5.1.2 — endpoints MUST NOT exceed
                // SETTINGS_MAX_CONCURRENT_STREAMS. We advertise this in our
                // local settings; new streams beyond the cap are refused so
                // the client can back off.
                if (_streams.Count >= _localSettings.MaxConcurrentStreams)
                {
                    throw new Http2StreamException(
                        frame.StreamId,
                        Http2ErrorCode.RefusedStream,
                        $"HTTP/2 stream {frame.StreamId} refused: server's SETTINGS_MAX_CONCURRENT_STREAMS ({_localSettings.MaxConcurrentStreams}) reached.");
                }
            }

            _lastInboundStreamId = Math.Max(_lastInboundStreamId, frame.StreamId);
            return GetOrCreateStreamLocked(frame.StreamId);
        }
    }

    private Http2Context? ProcessContinuationFrame(ReceivedFrame receivedFrame)
    {
        if (_continuationStreamId is null || _continuationStreamId.Value != receivedFrame.Frame.StreamId)
        {
            throw new Http2ConnectionException(
                Http2ErrorCode.ProtocolError,
                "The HTTP/2 CONTINUATION frame did not match the active header block stream.");
        }

        Http2Stream? stream;
        lock (_syncRoot)
        {
            if (!_streams.TryGetValue(receivedFrame.Frame.StreamId, out stream))
            {
                // The leading HEADERS frame must have created the stream entry;
                // an orphan CONTINUATION is a protocol error.
                throw new Http2ConnectionException(
                    Http2ErrorCode.ProtocolError,
                    $"HTTP/2 CONTINUATION frame received on unknown stream {receivedFrame.Frame.StreamId}.");
            }
        }

        stream.ReceiveContinuation(receivedFrame.Payload, receivedFrame.Frame.HeadersEndHeaders);

        if (receivedFrame.Frame.HeadersEndHeaders)
        {
            _continuationStreamId = null;
        }

        return TryDispatchStream(stream);
    }

    private async Task<Http2Context?> ProcessDataFrameAsync(ReceivedFrame receivedFrame, CancellationToken cancellationToken)
    {
        // RFC 9113 §6.1 — DATA frames MUST be associated with a stream;
        // stream 0 is reserved for connection-level frames.
        if (receivedFrame.Frame.StreamId == 0)
        {
            throw new Http2ConnectionException(
                Http2ErrorCode.ProtocolError,
                "HTTP/2 DATA frame received on stream 0.");
        }

        // RFC 9113 §6.1 — if the padding length equals the frame payload length
        // or is greater, it is a connection error of type PROTOCOL_ERROR. The
        // frame reader strips the single pad-length octet, so the remaining
        // payload (data + padding) must be at least the declared padding length.
        if (receivedFrame.Frame.DataPadLength > receivedFrame.Payload.Length)
        {
            throw new Http2ConnectionException(
                Http2ErrorCode.ProtocolError,
                $"HTTP/2 DATA frame on stream {receivedFrame.Frame.StreamId} declares {receivedFrame.Frame.DataPadLength} padding octets, which meets or exceeds the frame payload length.");
        }

        // RFC 9113 §5.2.2 / §6.9.1 — the DATA frame's *full payload length*
        // (including padding and the pad-length octet) counts against flow
        // control, even though only the de-padded data is delivered to the
        // application. We consume the connection window before we even know
        // which stream it belongs to so a misbehaving peer cannot exhaust the
        // connection window by sending DATA on rapidly-closed streams.
        int flowControlLength = receivedFrame.Frame.PayloadLength;

        Http2Stream? stream;
        bool recentlyClosed = false;
        lock (_syncRoot)
        {
            if (flowControlLength > 0 && !_connectionReceiveWindow.TryConsume(flowControlLength))
            {
                throw new Http2ConnectionException(
                    Http2ErrorCode.FlowControlError,
                    $"HTTP/2 DATA frame of {flowControlLength} octets exceeded the connection-level receive window.");
            }

            if (!_streams.TryGetValue(receivedFrame.Frame.StreamId, out stream))
            {
                // Disambiguate idle vs. recently-closed streams (RFC 9113 §5.1):
                //   - id > highest observed → truly idle stream, never opened.
                //   - id ≤ highest observed → previously opened stream we have
                //     since retired (END_STREAM + END_STREAM, or RST_STREAM).
                if (receivedFrame.Frame.StreamId > _lastInboundStreamId)
                {
                    throw new Http2ConnectionException(
                        Http2ErrorCode.ProtocolError,
                        $"HTTP/2 DATA frame received on idle stream {receivedFrame.Frame.StreamId}.");
                }

                recentlyClosed = true;
            }
            else if (flowControlLength > 0 && !stream.ReceiveWindow.TryConsume(flowControlLength))
            {
                // RFC 9113 §5.2.2 — the same DATA frame also counts against the
                // stream's receive window. Exhaustion is a stream error per §6.9.1.
                throw new Http2StreamException(
                    receivedFrame.Frame.StreamId,
                    Http2ErrorCode.FlowControlError,
                    $"HTTP/2 DATA frame of {flowControlLength} octets exceeded the stream {receivedFrame.Frame.StreamId} receive window.");
            }
        }

        if (recentlyClosed)
        {
            // We consumed the connection window above but are discarding this
            // DATA (the stream is gone). RFC 9113 §6.9 — credit the connection
            // window back so the peer's connection-level accounting does not
            // permanently shrink, then surface the stream error so a RST_STREAM
            // tells the peer to stop.
            if (flowControlLength > 0)
            {
                lock (_syncRoot)
                {
                    _connectionReceiveWindow.TryReplenish(flowControlLength);
                }

                await EmitWindowUpdateAsync(0, flowControlLength, cancellationToken).ConfigureAwait(false);
            }

            throw new Http2StreamException(
                receivedFrame.Frame.StreamId,
                Http2ErrorCode.StreamClosed,
                $"HTTP/2 DATA frame received on closed stream {receivedFrame.Frame.StreamId}.");
        }

        // Deliver the de-padded application data to the stream's body pipe,
        // tagged with its full flow-control cost so consumption credits the
        // correct amount back to the peer (RFC 9113 §6.9.1). The context was
        // already dispatched when the header block completed, so DATA never
        // produces a new context here.
        int dataLength = receivedFrame.Payload.Length - receivedFrame.Frame.DataPadLength;
        ReadOnlyMemory<byte> data = dataLength > 0
            ? receivedFrame.Payload.AsMemory(0, dataLength)
            : ReadOnlyMemory<byte>.Empty;

        stream!.ReceiveData(data, flowControlLength, receivedFrame.Frame.DataEndStream);
        return null;
    }

    private async Task ProcessPingFrameAsync(ReceivedFrame receivedFrame, CancellationToken cancellationToken)
    {
        if (receivedFrame.Frame.PingAck)
        {
            return;
        }

        Http2Frame acknowledgement = new();
        acknowledgement.PreparePing(Http2PingFrameFlags.Acknowledge);
        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await Http2FrameWriter.WriteAsync(Stream, acknowledgement, receivedFrame.Payload, cancellationToken).ConfigureAwait(false);
            await Stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <summary>
    /// Dispatches a stream's request head as an <c>IHttpContext</c> once its header
    /// block is complete, exactly once. The body streams in afterward through the
    /// stream's body pipe (RFC 9113 lets the server respond before the request body
    /// finishes arriving).
    /// </summary>
    private Http2Context? TryDispatchStream(Http2Stream stream)
    {
        if (!stream.IsHeadReady)
        {
            return null;
        }

        stream.ContextDispatched = true;

        // RFC 9113 §5.1 — the stream stays in the active map after dispatch:
        // it is in HalfClosedRemote (or Open, if the body is still arriving),
        // and a subsequent RST_STREAM from the peer needs to find it so it can
        // fire RequestAborted on the application's IHttpContext.
        try
        {
            return stream.CreateContext(_headerDecoder, ConnectionInfo, GetScheme(), CancellationToken.None, this);
        }
        catch (HPack.HPackDecodingException error)
        {
            // RFC 9113 §8.2 / §8.3 — malformed field sections (illegal
            // pseudo-header order, forbidden connection-specific
            // fields, etc.) are connection-level PROTOCOL_ERRORs. Wrap
            // the HPack-level exception so the pump emits GOAWAY before
            // tearing down.
            throw new Http2ConnectionException(
                Http2ErrorCode.ProtocolError,
                $"HTTP/2 HEADERS frame contained a malformed field section: {error.Message}");
        }
    }

    /// <summary>
    /// Fetches or creates the stream for <paramref name="streamId"/>. Must be
    /// called while holding <see cref="_syncRoot"/>.
    /// </summary>
    private Http2Stream GetOrCreateStreamLocked(int streamId)
    {
        if (!_streams.TryGetValue(streamId, out Http2Stream? stream))
        {
            // RFC 9113 §6.9.2 — new streams inherit their initial windows
            // from the peer-advertised SETTINGS_INITIAL_WINDOW_SIZE on the
            // send side and our locally-advertised value on the receive
            // side. Subsequent SETTINGS frames adjust existing windows by
            // delta; new streams always pick up the current snapshot.
            stream = new Http2Stream(
                streamId,
                initialSendWindow: _remoteSettings.InitialWindowSize,
                initialReceiveWindow: _localSettings.InitialWindowSize);
            _streams.Add(streamId, stream);
        }

        return stream;
    }

    /// <summary>
    /// Removes a stream and credits its outstanding receive-window debt back to the
    /// connection window, telling the peer via a connection-level <c>WINDOW_UPDATE</c>.
    /// </summary>
    /// <remarks>
    /// RFC 9113 §5.2 / §6.9 — because request-body credit is consumption-driven, a
    /// stream that is removed while the application never drained its buffered body
    /// (an ignored body, a reset, an abandoned upload) still holds octets against
    /// the shared connection receive window. Those octets are now discarded, so the
    /// connection window MUST be credited back or it would shrink permanently over
    /// the connection's lifetime and eventually stall inbound DATA on every stream.
    /// The debt is exactly <c>InitialReceiveWindow - ReceiveWindow.Available</c> —
    /// what was consumed on receipt minus what consumption already credited.
    /// </remarks>
    private async ValueTask<Http2Stream?> RemoveStreamAsync(int streamId, CancellationToken cancellationToken)
    {
        int reclaimed = 0;
        Http2Stream? stream;
        lock (_syncRoot)
        {
            if (!_streams.TryGetValue(streamId, out stream))
            {
                return null;
            }

            if (!stream.ReceiveReclaimed)
            {
                stream.ReceiveReclaimed = true;
                long outstanding = stream.InitialReceiveWindow - stream.ReceiveWindow.Available;
                if (outstanding > 0)
                {
                    reclaimed = (int)Math.Min(outstanding, int.MaxValue);
                    _connectionReceiveWindow.TryReplenish(reclaimed);
                }
            }

            _streams.Remove(streamId);
        }

        if (reclaimed > 0)
        {
            await EmitWindowUpdateAsync(0, reclaimed, cancellationToken).ConfigureAwait(false);
        }

        return stream;
    }

    private async Task WriteHeaderBlockAsync(int streamId, byte[] headerBlock, bool endStream, CancellationToken cancellationToken)
    {
        int offset = 0;
        bool firstFrame = true;

        do
        {
            int remaining = headerBlock.Length - offset;
            int chunkLength = remaining > 0 ? Math.Min((int)_remoteSettings.MaxFrameSize, remaining) : 0;
            Http2Frame frame = new();

            if (firstFrame)
            {
                Http2HeadersFrameFlags flags = remaining <= chunkLength ? Http2HeadersFrameFlags.EndHeaders : Http2HeadersFrameFlags.None;

                if (endStream)
                {
                    flags |= Http2HeadersFrameFlags.EndStream;
                }

                frame.PrepareHeaders(flags, streamId);
            }
            else
            {
                frame.Type = Http2FrameType.Continuation;
                frame.Flags = remaining <= chunkLength ? (byte)Http2HeadersFrameFlags.EndHeaders : (byte)0;
                frame.StreamId = streamId;
            }

            await Http2FrameWriter.WriteAsync(
                Stream,
                frame,
                chunkLength == 0 ? ReadOnlyMemory<byte>.Empty : headerBlock.AsMemory(offset, chunkLength),
                cancellationToken).ConfigureAwait(false);

            offset += chunkLength;
            firstFrame = false;
        }
        while (offset < headerBlock.Length || firstFrame);
    }

    private async Task WriteBodyAsync(int streamId, byte[] bodyBytes, CancellationToken cancellationToken)
    {
        int offset = 0;

        while (offset < bodyBytes.Length)
        {
            int chunkLength = Math.Min((int)_remoteSettings.MaxFrameSize, bodyBytes.Length - offset);
            Http2Frame frame = new();
            frame.PrepareData(streamId);

            if (offset + chunkLength >= bodyBytes.Length)
            {
                frame.DataFlags |= Http2DataFrameFlags.EndStream;
            }

            await Http2FrameWriter.WriteAsync(Stream, frame, bodyBytes.AsMemory(offset, chunkLength), cancellationToken).ConfigureAwait(false);
            offset += chunkLength;
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

    private static async Task<byte[]?> ReadExactOrNullAsync(Stream stream, int count, CancellationToken cancellationToken)
    {
        byte[] buffer = new byte[count];
        int offset = 0;

        while (offset < count)
        {
            int bytesRead = await stream.ReadAsync(buffer.AsMemory(offset, count - offset), cancellationToken).ConfigureAwait(false);

            if (bytesRead == 0)
            {
                return offset == 0 ? null : throw new EndOfStreamException("The HTTP/2 stream ended before a complete frame header was received.");
            }

            offset += bytesRead;
        }

        return buffer;
    }

    private static async Task<byte[]> ReadExactOrThrowAsync(Stream stream, int count, CancellationToken cancellationToken)
    {
        byte[]? buffer = await ReadExactOrNullAsync(stream, count, cancellationToken).ConfigureAwait(false);

        if (buffer is null)
        {
            throw new EndOfStreamException("The HTTP/2 stream ended before the expected data was received.");
        }

        return buffer;
    }

    private static async Task<ReceivedFrame?> ReadFrameAsync(Stream stream, uint maxFrameSize, CancellationToken cancellationToken)
    {
        byte[]? header = await ReadExactOrNullAsync(stream, Http2FrameReader.HeaderLength, cancellationToken).ConfigureAwait(false);

        if (header is null)
        {
            return null;
        }

        int payloadLength = (header[0] << 16) | (header[1] << 8) | header[2];

        // RFC 9113 §4.2 — a frame whose length exceeds the receiver's
        // advertised MAX_FRAME_SIZE is a connection error with code
        // FRAME_SIZE_ERROR.
        if (payloadLength > maxFrameSize)
        {
            throw new Http2ConnectionException(
                Http2ErrorCode.FrameSizeError,
                $"HTTP/2 frame payload length {payloadLength} exceeded the advertised maximum {maxFrameSize}.");
        }

        byte[] buffer = new byte[Http2FrameReader.HeaderLength + payloadLength];
        Buffer.BlockCopy(header, 0, buffer, 0, header.Length);

        if (payloadLength > 0)
        {
            byte[] payload = await ReadExactOrThrowAsync(stream, payloadLength, cancellationToken).ConfigureAwait(false);
            Buffer.BlockCopy(payload, 0, buffer, Http2FrameReader.HeaderLength, payload.Length);
        }

        ReadOnlySequence<byte> sequence = new(buffer);
        Http2Frame frame = new();

        if (!Http2FrameReader.TryReadFrame(ref sequence, frame, maxFrameSize, out ReadOnlySequence<byte> payloadSequence))
        {
            throw new Http2ConnectionException(
                Http2ErrorCode.ProtocolError,
                "The HTTP/2 frame could not be parsed from the buffered payload.");
        }

        return new ReceivedFrame(frame, payloadSequence.IsEmpty ? Array.Empty<byte>() : payloadSequence.ToArray());
    }

    /// <summary>
    /// Emits a <c>WINDOW_UPDATE</c> frame for <paramref name="streamId"/>
    /// (stream 0 for connection-level credit) crediting
    /// <paramref name="increment"/> octets back to the peer (RFC 9113 §6.9).
    /// </summary>
    private async Task EmitWindowUpdateAsync(int streamId, int increment, CancellationToken cancellationToken)
    {
        if (increment <= 0)
        {
            return;
        }

        try
        {
            Http2Frame frame = new();
            frame.PrepareWindowUpdate(streamId, increment);
            await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await Http2FrameWriter.WriteAsync(Stream, frame, ReadOnlyMemory<byte>.Empty, cancellationToken).ConfigureAwait(false);
                await Stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                _writeLock.Release();
            }
        }
        catch
        {
            // Best-effort: if the wire is already down, the connection-level
            // teardown path will surface the error.
        }
    }

    /// <summary>
    /// Emits a <c>RST_STREAM</c> frame for <paramref name="streamId"/> with
    /// <paramref name="errorCode"/> (RFC 9113 §6.4 / §5.4.2). Removes the
    /// stream from our active map so subsequent frames on it surface as
    /// "unknown stream" errors at the connection level.
    /// </summary>
    private async Task EmitRstStreamAsync(int streamId, Http2ErrorCode errorCode, CancellationToken cancellationToken)
    {
        try
        {
            Http2Frame rstStream = new();
            rstStream.PrepareRstStream(streamId, errorCode);
            await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await Http2FrameWriter.WriteAsync(Stream, rstStream, ReadOnlyMemory<byte>.Empty, cancellationToken).ConfigureAwait(false);
                await Stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                _writeLock.Release();
            }
        }
        catch
        {
            // Best-effort: stream errors are recoverable, and if the wire
            // is already in trouble the next connection-level read will
            // surface that via GOAWAY.
        }

        Http2Stream? stream = await RemoveStreamAsync(streamId, cancellationToken).ConfigureAwait(false);
        stream?.SendReset();

        // NOTE: _continuationStreamId is deliberately NOT touched here. This method
        // is callable from the application thread (SendAsync's abandoned-body /
        // cancel reset) concurrently with the pump, and _continuationStreamId is a
        // non-atomic int? owned solely by the pump. A stream can never be both
        // mid-CONTINUATION (header block incomplete) and dispatched-and-responded-
        // to, so the app path never targets the active continuation stream; the
        // pump resets the field itself in ProcessRstStreamFrame when the peer
        // resets a mid-continuation stream.
    }

    /// <summary>
    /// Emits a <c>GOAWAY</c> frame on stream 0 carrying
    /// <paramref name="errorCode"/> and the highest stream ID we have
    /// observed so far (RFC 9113 §6.8). The pump then propagates the
    /// exception so the listener can tear the connection down.
    /// </summary>
    private async Task EmitGoAwayAsync(Http2ErrorCode errorCode, CancellationToken cancellationToken)
    {
        try
        {
            Http2Frame goAway = new();
            goAway.PrepareGoAway(_lastInboundStreamId, errorCode);
            await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await Http2FrameWriter.WriteAsync(Stream, goAway, ReadOnlyMemory<byte>.Empty, cancellationToken).ConfigureAwait(false);
                await Stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                _writeLock.Release();
            }
        }
        catch
        {
            // Best-effort: if the wire is already dead we can't deliver
            // the GOAWAY. The connection error will still surface to the
            // listener through the original exception.
        }
    }

    /// <summary>
    /// Builds the explicit server-side initial <see cref="Http2ConnectionSettings"/>
    /// we advertise to peers. The defaults mostly track the RFC 9113
    /// initial values except for ENABLE_PUSH (0 — Cohesion does not
    /// implement server push) and MAX_HEADER_LIST_SIZE (a 16 KB DoS
    /// guard instead of "unlimited").
    /// </summary>
    private static Http2ConnectionSettings BuildLocalSettings()
    {
        return new Http2ConnectionSettings
        {
            HeaderTableSize = Http2ConnectionSettings.InitialHeaderTableSize,
            EnablePush = 0,
            MaxConcurrentStreams = 100,
            InitialWindowSize = Http2ConnectionSettings.InitialInitialWindowSize,
            MaxFrameSize = Http2ConnectionSettings.InitialMaxFrameSize,
            MaxHeaderListSize = 16_384,
            // RFC 8441 §3 — advertise support for the extended CONNECT protocol
            // so peers may bootstrap WebSocket (and other protocols) over a
            // single stream via CONNECT + :protocol.
            EnableConnectProtocol = 1,
        };
    }

    /// <summary>
    /// Serialises <see cref="_localSettings"/> as a SETTINGS-frame
    /// payload (6 octets per parameter; identifier first, then value;
    /// both big-endian per RFC 9113 §6.5.1).
    /// </summary>
    private byte[] EncodeLocalSettings()
    {
        // Six parameters that change from the spec's initial values OR are
        // explicit choices we want the peer to see. We emit all of them
        // every time so the peer never has to guess our intent.
        (Http2SettingsParameter Parameter, uint Value)[] entries =
        {
            (Http2SettingsParameter.SETTINGS_HEADER_TABLE_SIZE, _localSettings.HeaderTableSize),
            (Http2SettingsParameter.SETTINGS_ENABLE_PUSH, _localSettings.EnablePush),
            (Http2SettingsParameter.SETTINGS_MAX_CONCURRENT_STREAMS, _localSettings.MaxConcurrentStreams),
            (Http2SettingsParameter.SETTINGS_INITIAL_WINDOW_SIZE, _localSettings.InitialWindowSize),
            (Http2SettingsParameter.SETTINGS_MAX_FRAME_SIZE, _localSettings.MaxFrameSize),
            (Http2SettingsParameter.SETTINGS_MAX_HEADER_LIST_SIZE, _localSettings.MaxHeaderListSize),
            // RFC 8441 §3 — SETTINGS_ENABLE_CONNECT_PROTOCOL = 1 tells the peer
            // it MAY use extended CONNECT (CONNECT + :protocol).
            (Http2SettingsParameter.SETTINGS_ENABLE_CONNECT_PROTOCOL, _localSettings.EnableConnectProtocol),
        };

        byte[] payload = new byte[entries.Length * Http2FrameReader.SettingSize];
        Span<byte> span = payload;

        for (int index = 0; index < entries.Length; index++)
        {
            System.Buffers.Binary.BinaryPrimitives.WriteUInt16BigEndian(span, (ushort)entries[index].Parameter);
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(span.Slice(2), entries[index].Value);
            span = span.Slice(Http2FrameReader.SettingSize);
        }

        return payload;
    }

    /// <summary>
    /// Performs an RFC 9113 §6.8 graceful close: stops the frame pump, emits a
    /// <c>GOAWAY</c> frame carrying <see cref="Http2ErrorCode.NoError"/>, then
    /// signals the transport's send pipeline that no further bytes will be
    /// written. The transport's send task reads the remaining bytes, performs its
    /// final <c>Socket.SendAsync</c> (which waits for the kernel to accept the
    /// bytes), then exits — at which point the underlying socket is torn down
    /// through the transport's own teardown path.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Closes the gap identified by #686: previously, <see cref="Http2Connection.DisposeAsync"/>
    /// invoked <c>IConnection.DisposeAsync</c> directly, which
    /// aborts the socket immediately and can race the send task's
    /// in-flight <c>Socket.SendAsync</c>. Completing the output writer
    /// here lets the send task drain naturally; the caller then waits
    /// for the connection state to transition out of <c>Open</c> before
    /// declaring the close complete.
    /// </para>
    /// <para>
    /// Idempotent — repeated invocations after the first are no-ops.
    /// </para>
    /// </remarks>
    public async ValueTask GracefulCloseAsync(CancellationToken cancellationToken = default)
    {
        if (Interlocked.Exchange(ref _gracefulCloseStarted, 1) == 1)
        {
            return;
        }

        // Stop the frame pump first so it cannot race the GOAWAY write or keep
        // reading after we announce the close. Cancelling unblocks its in-flight
        // wire read; awaiting it guarantees no further inbound processing.
        await StopPumpAsync().ConfigureAwait(false);

        // EnsureInitializedAsync may not have run if the pump never started. Skip
        // GOAWAY in that case; the peer has not seen any HTTP/2 traffic from us
        // yet, so closing the underlying socket is enough.
        if (!_initialized)
        {
            await CompleteOutputAsync().ConfigureAwait(false);
            return;
        }

        try
        {
            await EmitGoAwayAsync(Http2ErrorCode.NoError, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            await CompleteOutputAsync().ConfigureAwait(false);
        }
    }

    private async ValueTask StopPumpAsync()
    {
        _pumpCts?.Cancel();

        if (_pumpTask is not null)
        {
            try
            {
                await _pumpTask.ConfigureAwait(false);
            }
            catch
            {
                // The pump isolates its own failures; awaiting it here is only to
                // guarantee it has stopped before we write GOAWAY.
            }
        }
    }

    private ValueTask CompleteOutputAsync()
    {
        try
        {
            // RFC 9113 §6.8 — completing the connection's output writer
            // signals the transport's send loop that no further bytes will
            // be queued. The send loop processes its remaining backlog (one
            // final Socket.SendAsync, which waits for the kernel to ACK)
            // and then exits — closing the socket cleanly.
            return Connection.Output.CompleteAsync();
        }
        catch
        {
            // The writer may already be completed if a teardown ran
            // concurrently. Idempotent.
            return ValueTask.CompletedTask;
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await GracefulCloseAsync().ConfigureAwait(false);
        _pumpCts?.Dispose();
        _writeLock.Dispose();
    }

    private readonly struct ReceivedFrame
    {
        public ReceivedFrame(Http2Frame frame, byte[] payload)
        {
            Frame = frame;
            Payload = payload;
        }

        public Http2Frame Frame { get; }

        public byte[] Payload { get; }
    }
}
