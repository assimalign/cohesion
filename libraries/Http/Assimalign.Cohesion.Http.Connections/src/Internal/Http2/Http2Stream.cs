using System;
using System.IO;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

using Assimalign.Cohesion.Http.Connections.Internal.Http2.HPack;

namespace Assimalign.Cohesion.Http.Connections.Internal.Http2;

/// <summary>
/// Server-side HTTP/2 stream — tracks the RFC 9113 §5.1 lifecycle state
/// plus the accumulating header block and the streaming body pipe fed from
/// the peer's DATA frames.
/// </summary>
/// <remarks>
/// <para>
/// State transitions are explicit and enforced by the <c>Receive*</c> /
/// <c>Send*</c> methods on this type. Any frame that is illegal in the
/// current state surfaces as either a
/// <see cref="Http2StreamException"/> (stream-level — RST_STREAM keeps
/// the connection alive) or an <see cref="Http2ConnectionException"/>
/// (connection-level — GOAWAY tears the connection down).
/// </para>
/// <para>
/// The body is delivered incrementally through <see cref="_bodyChannel"/>: the
/// frame pump writes each decoded DATA payload here as it arrives, and the
/// application drains it through <see cref="Http2RequestBodyStream"/>. This is
/// what keeps buffered request-body bytes bounded by the advertised receive
/// window and lets application consumption — not receipt — drive
/// <c>WINDOW_UPDATE</c> emission. The pump (single writer) and the request
/// handler (single reader) run concurrently, so state transitions are guarded by
/// <see cref="_stateLock"/> to keep the local (send) and remote (receive) halves
/// from racing each other.
/// </para>
/// </remarks>
internal sealed class Http2Stream
{
    private readonly MemoryStream _headerBlock;
    // RFC 9113 §5.2 — inbound DATA is queued here for the application to drain
    // rather than buffered whole before dispatch. Unbounded at the channel level
    // (the flow-control window is the real bound); single-writer (the frame pump)
    // and single-reader (the request handler).
    private readonly Channel<Http2DataChunk> _bodyChannel;
    // Guards State transitions: the pump mutates the remote half (Receive*) while
    // the request handler mutates the local half (Send*), concurrently.
    private readonly object _stateLock = new();
    // RFC 9113 §5.4.2 — when the peer resets a stream (or we decide to
    // reset it locally), the application MUST be able to learn that its
    // request was abandoned. This token source backs IHttpContext's
    // RequestAborted and is fired whenever the stream is reset.
    private readonly CancellationTokenSource _abortedSource = new();
    // RFC 9218 §8 — a PRIORITY_UPDATE frame takes precedence over the
    // Priority request header for the same stream, regardless of arrival
    // order. Once an update is applied, a later header parse must not
    // clobber it.
    private bool _priorityFromUpdate;

    // 0 = body channel still open, 1 = completed. Interlocked because both the
    // pump (END_STREAM / peer reset) and the request handler (local reset on an
    // undrained body) can race to complete it.
    private int _bodyCompleted;

    /// <summary>
    /// Send-side flow-control window — the number of DATA octets we
    /// may transmit on this stream before the peer credits us with a
    /// <c>WINDOW_UPDATE</c>. Initialised from the peer's advertised
    /// <c>SETTINGS_INITIAL_WINDOW_SIZE</c>.
    /// </summary>
    public Http2FlowControlWindow SendWindow;

    /// <summary>
    /// Receive-side flow-control window — the number of DATA octets the
    /// peer may transmit on this stream before we send a
    /// <c>WINDOW_UPDATE</c>. Initialised from our local
    /// <c>SETTINGS_INITIAL_WINDOW_SIZE</c> and only replenished as the
    /// application consumes the body.
    /// </summary>
    public Http2FlowControlWindow ReceiveWindow;

    /// <summary>
    /// The initial receive-window size, retained so that when the stream is
    /// removed the connection can reclaim its outstanding receive debt
    /// (initial minus current available = octets consumed from the shared
    /// connection window that the application never drained).
    /// </summary>
    public long InitialReceiveWindow { get; }

    /// <summary>
    /// Whether this stream's outstanding receive-window debt has already been
    /// credited back to the connection window on removal. Guarded by the
    /// connection's synchronization root, not this stream's state lock, because
    /// it coordinates the pump's stream removal with the body reader's
    /// consumption crediting.
    /// </summary>
    public bool ReceiveReclaimed { get; set; }

    public Http2Stream(int streamId, long initialSendWindow, long initialReceiveWindow)
    {
        StreamId = streamId;
        InitialReceiveWindow = initialReceiveWindow;
        _headerBlock = new MemoryStream();
        // SingleWriter is false: the frame pump writes DATA chunks (ReceiveData),
        // but a local reset from the application thread (SendReset via an
        // abandoned-body / cancel RST_STREAM) can complete the writer concurrently
        // with an in-flight pump write. SingleReader is true — exactly one request
        // handler drains the body.
        _bodyChannel = Channel.CreateUnbounded<Http2DataChunk>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false,
            AllowSynchronousContinuations = false,
        });
        State = Http2StreamState.Idle;
        SendWindow = new Http2FlowControlWindow(initialSendWindow);
        ReceiveWindow = new Http2FlowControlWindow(initialReceiveWindow);
    }

    /// <summary>
    /// A token that fires when this stream is reset (locally or by the
    /// peer). Wired into <see cref="IHttpContext.RequestCancelled"/> so
    /// the application can observe peer cancellation.
    /// </summary>
    public CancellationToken RequestAborted => _abortedSource.Token;

    /// <summary>The stream identifier (RFC 9113 §5.1.1).</summary>
    public int StreamId { get; }

    /// <summary>
    /// The effective RFC 9218 priority the connection engine uses to schedule this
    /// stream's response write. Initialised to <see cref="HttpPriority.Default"/>
    /// (urgency 3, non-incremental), refined from the <c>Priority</c> request
    /// header when the request materialises, and overridden by any
    /// <c>PRIORITY_UPDATE</c> frame (RFC 9218 §8).
    /// </summary>
    public HttpPriority EffectivePriority { get; private set; } = HttpPriority.Default;

    /// <summary>Current lifecycle state per RFC 9113 §5.1.</summary>
    public Http2StreamState State { get; private set; }

    /// <summary><see langword="true"/> once <c>END_HEADERS</c> has been observed across HEADERS/CONTINUATION.</summary>
    public bool HeadersCompleted { get; private set; }

    /// <summary><see langword="true"/> once the request side has been closed (END_STREAM from the peer or RST_STREAM).</summary>
    public bool InputCompleted { get; private set; }

    /// <summary>
    /// <see langword="true"/> once the request head has been dispatched to the
    /// application as an <c>IHttpContext</c>. The head is dispatched as soon as the
    /// header block is complete (RFC 9113 lets the server respond before the body
    /// arrives), so the body streams in afterward; this flag stops the pump from
    /// dispatching the same stream twice as later DATA frames arrive.
    /// </summary>
    public bool ContextDispatched { get; set; }

    /// <summary>
    /// The stream is ready to materialise as an <c>IHttpContext</c> once its header
    /// block is complete and it has not already been dispatched or closed.
    /// </summary>
    public bool IsHeadReady => HeadersCompleted && !ContextDispatched && State != Http2StreamState.Closed;

    /// <summary>
    /// Whether the stream has reached the terminal <see cref="Http2StreamState.Closed"/>
    /// state.
    /// </summary>
    public bool IsClosed
    {
        get
        {
            lock (_stateLock)
            {
                return State == Http2StreamState.Closed;
            }
        }
    }

    /// <summary>
    /// Folds an inbound HEADERS / CONTINUATION payload into the accumulating header
    /// block, applies the RFC 9113 §5.1 state transition driven by HEADERS, and
    /// returns when the header block has been fully assembled.
    /// </summary>
    /// <param name="payload">The decoded payload bytes (no padding / no priority data).</param>
    /// <param name="endHeaders">Whether the inbound frame carried the END_HEADERS flag.</param>
    /// <param name="endStream">
    /// Whether the inbound frame carried the END_STREAM flag. Only meaningful on
    /// the leading HEADERS frame; CONTINUATION frames do not carry END_STREAM.
    /// </param>
    /// <exception cref="Http2ConnectionException">
    /// Thrown when HEADERS is received on a stream that is in a state where
    /// HEADERS is illegal (e.g. <see cref="Http2StreamState.Closed"/> after a
    /// reset that was not initiated by the peer).
    /// </exception>
    public void ReceiveHeaders(ReadOnlySpan<byte> payload, bool endHeaders, bool endStream)
    {
        lock (_stateLock)
        {
            // RFC 9113 §5.1 — HEADERS is legal in:
            //   - Idle: opens the stream (→ Open or → HalfClosedRemote if END_STREAM)
            //   - Open / HalfClosedLocal: continuation of the request (trailers)
            // Anything else is a connection error per RFC 9113 §5.1 (PROTOCOL_ERROR
            // for an unexpected HEADERS on a closed stream, STREAM_CLOSED for one
            // on a stream we already saw END_STREAM for).
            switch (State)
            {
                case Http2StreamState.Idle:
                    State = endStream ? Http2StreamState.HalfClosedRemote : Http2StreamState.Open;
                    break;
                case Http2StreamState.Open:
                case Http2StreamState.HalfClosedLocal:
                    // Trailers — the peer is wrapping up its half. END_STREAM
                    // must be set on a trailing HEADERS frame (RFC 9113 §8.1).
                    if (!endStream)
                    {
                        throw new Http2ConnectionException(
                            Http2ErrorCode.ProtocolError,
                            $"HTTP/2 trailing HEADERS on stream {StreamId} must carry END_STREAM.");
                    }

                    State = State == Http2StreamState.Open
                        ? Http2StreamState.HalfClosedRemote
                        : Http2StreamState.Closed;
                    break;
                case Http2StreamState.HalfClosedRemote:
                case Http2StreamState.Closed:
                    throw new Http2ConnectionException(
                        Http2ErrorCode.StreamClosed,
                        $"HTTP/2 HEADERS frame received on stream {StreamId} in state {State}; the peer has already closed its half.");
            }
        }

        AppendHeaderBytes(payload);

        if (endHeaders)
        {
            HeadersCompleted = true;
        }

        if (endStream)
        {
            InputCompleted = true;
            CompleteBody();
        }
    }

    /// <summary>
    /// Folds an inbound CONTINUATION payload into the accumulating header block.
    /// CONTINUATION cannot change stream state — it only continues a header block
    /// that an earlier HEADERS frame opened (RFC 9113 §6.10).
    /// </summary>
    /// <exception cref="Http2ConnectionException">
    /// Thrown when CONTINUATION is received on a stream that is not currently in
    /// a state that expects continuation frames.
    /// </exception>
    public void ReceiveContinuation(ReadOnlySpan<byte> payload, bool endHeaders)
    {
        // CONTINUATION is only legal mid-header-block on a stream that has
        // already received its leading HEADERS frame. The connection-level
        // continuation tracking guards the cross-stream rule; here we just
        // assert that the stream itself is in a sane state.
        lock (_stateLock)
        {
            if (State == Http2StreamState.Idle || State == Http2StreamState.Closed)
            {
                throw new Http2ConnectionException(
                    Http2ErrorCode.ProtocolError,
                    $"HTTP/2 CONTINUATION frame received on stream {StreamId} in state {State}.");
            }
        }

        AppendHeaderBytes(payload);

        if (endHeaders)
        {
            HeadersCompleted = true;
        }
    }

    /// <summary>
    /// Queues an inbound DATA payload onto the body pipe and applies the END_STREAM
    /// transition.
    /// </summary>
    /// <param name="data">The de-padded application data.</param>
    /// <param name="flowControlLength">
    /// The DATA frame's full payload length (including padding) — the octets to
    /// credit back to the peer once the application consumes this chunk.
    /// </param>
    /// <param name="endStream">Whether the frame carried END_STREAM.</param>
    /// <exception cref="Http2StreamException">
    /// Thrown when DATA is received on a stream that has already had its remote
    /// half closed (STREAM_CLOSED — RFC 9113 §5.1).
    /// </exception>
    /// <exception cref="Http2ConnectionException">
    /// Thrown when DATA is received on a stream in <see cref="Http2StreamState.Idle"/>
    /// (no HEADERS yet) — that is a PROTOCOL_ERROR connection-level fault.
    /// </exception>
    public void ReceiveData(ReadOnlyMemory<byte> data, int flowControlLength, bool endStream)
    {
        lock (_stateLock)
        {
            switch (State)
            {
                case Http2StreamState.Idle:
                    throw new Http2ConnectionException(
                        Http2ErrorCode.ProtocolError,
                        $"HTTP/2 DATA frame received on stream {StreamId} before HEADERS.");
                case Http2StreamState.Open:
                    if (endStream)
                    {
                        State = Http2StreamState.HalfClosedRemote;
                        InputCompleted = true;
                    }
                    break;
                case Http2StreamState.HalfClosedLocal:
                    if (endStream)
                    {
                        State = Http2StreamState.Closed;
                        InputCompleted = true;
                    }
                    break;
                case Http2StreamState.HalfClosedRemote:
                case Http2StreamState.Closed:
                    // The peer is forbidden from sending DATA after it sent
                    // END_STREAM or after we reset the stream.
                    throw new Http2StreamException(
                        StreamId,
                        Http2ErrorCode.StreamClosed,
                        $"HTTP/2 DATA frame received on stream {StreamId} in state {State}.");
            }
        }

        if (!data.IsEmpty || flowControlLength > 0)
        {
            _bodyChannel.Writer.TryWrite(new Http2DataChunk(data, flowControlLength));
        }

        if (endStream)
        {
            CompleteBody();
        }
    }

    /// <summary>
    /// Applies an inbound RST_STREAM — moves the stream to
    /// <see cref="Http2StreamState.Closed"/>, marks both halves as
    /// finalised, and fires <see cref="RequestAborted"/> so any
    /// application code reading the request body or running the
    /// handler observes the cancellation.
    /// </summary>
    /// <exception cref="Http2ConnectionException">
    /// RST_STREAM on an idle stream is a connection error
    /// (RFC 9113 §6.4).
    /// </exception>
    public void ReceiveReset()
    {
        lock (_stateLock)
        {
            if (State == Http2StreamState.Idle)
            {
                throw new Http2ConnectionException(
                    Http2ErrorCode.ProtocolError,
                    $"HTTP/2 RST_STREAM received on idle stream {StreamId}.");
            }

            State = Http2StreamState.Closed;
            InputCompleted = true;
        }

        CompleteBody();
        TryFireAbort();
    }

    /// <summary>
    /// Marks the stream's local half as closed because the server is about
    /// to send a frame carrying END_STREAM (the typical case is the response
    /// body's terminating DATA frame, or a header-only response).
    /// </summary>
    public void SendEndStream()
    {
        lock (_stateLock)
        {
            State = State switch
            {
                Http2StreamState.Open => Http2StreamState.HalfClosedLocal,
                Http2StreamState.HalfClosedRemote => Http2StreamState.Closed,
                // Idempotent / no-op for already-closed states.
                _ => State,
            };
        }
    }

    /// <summary>
    /// Marks the stream as closed because the server emitted a RST_STREAM,
    /// and fires <see cref="RequestAborted"/> so the application sees
    /// the cancellation.
    /// </summary>
    public void SendReset()
    {
        lock (_stateLock)
        {
            State = Http2StreamState.Closed;
            InputCompleted = true;
        }

        CompleteBody();
        TryFireAbort();
    }

    private void TryFireAbort()
    {
        try
        {
            _abortedSource.Cancel();
        }
        catch (System.ObjectDisposedException)
        {
            // The CTS may have been disposed already if a downstream
            // consumer raced our reset. Idempotent.
        }
    }

    /// <summary>
    /// Marks the request side as complete without consuming additional bytes.
    /// Used when an inbound HEADERS frame carries END_STREAM (a request with
    /// no body).
    /// </summary>
    public void CompleteInput()
    {
        InputCompleted = true;
        CompleteBody();
    }

    /// <summary>
    /// Aborts the request when the connection tears down (wire failure, connection
    /// error, or cooperative shutdown) while the body is still incoming. Fires
    /// <see cref="RequestAborted"/> and completes the body pipe so a handler parked
    /// reading the body observes cancellation — not a clean end-of-stream, which
    /// would let it mistake a truncated body for a complete one.
    /// </summary>
    public void AbortOnShutdown()
    {
        CompleteBody();

        // Only a body that was still incoming is truncated. A fully-received body
        // (END_STREAM already observed) is complete and buffered; the handler must
        // still be able to read it, so do NOT fire the abort for it.
        if (!InputCompleted)
        {
            TryFireAbort();
        }
    }

    private void CompleteBody()
    {
        // Idempotent: END_STREAM, a peer reset, a local reset, and connection
        // shutdown can all reach here concurrently.
        if (Interlocked.Exchange(ref _bodyCompleted, 1) == 0)
        {
            _bodyChannel.Writer.TryComplete();
        }
    }

    /// <summary>
    /// Applies a priority derived from the <c>Priority</c> request header. It is a
    /// no-op once a <c>PRIORITY_UPDATE</c> has been applied, because an update
    /// takes precedence over the header regardless of order (RFC 9218 §8).
    /// </summary>
    /// <param name="priority">The header-derived priority.</param>
    public void SetPriorityFromHeader(HttpPriority priority)
    {
        if (!_priorityFromUpdate)
        {
            EffectivePriority = priority;
        }
    }

    /// <summary>
    /// Applies a priority carried by a <c>PRIORITY_UPDATE</c> frame. This overrides
    /// any header-derived value and pins the effective priority so a subsequently
    /// parsed header cannot clobber it (RFC 9218 §8).
    /// </summary>
    /// <param name="priority">The update-derived priority.</param>
    public void ApplyPriorityUpdate(HttpPriority priority)
    {
        EffectivePriority = priority;
        _priorityFromUpdate = true;
    }

    private void AppendHeaderBytes(ReadOnlySpan<byte> payload)
    {
        if (!payload.IsEmpty)
        {
            _headerBlock.Write(payload);
        }
    }

    public Http2Context CreateContext(
        HPackDecoder decoder,
        HttpConnectionInfo connectionInfo,
        HttpScheme fallbackScheme,
        CancellationToken connectionAborted,
        Func<int, int, CancellationToken, ValueTask> onBodyConsumed)
    {
        if (!HeadersCompleted)
        {
            throw new InvalidOperationException("The HTTP/2 stream is not ready to create a request context.");
        }

        // RFC 9113 §5.4.2 — RequestAborted fires when either the connection
        // is being torn down (connectionAborted token) OR this specific
        // stream is reset (our internal _abortedSource via RequestAborted).
        // Link them so the application sees a single token that fires on
        // either condition.
        CancellationToken requestAborted = connectionAborted == default
            ? RequestAborted
            : CancellationTokenSource.CreateLinkedTokenSource(connectionAborted, RequestAborted).Token;

        HPackDecodedHeaders decodedHeaders = decoder.DecodeRequestHeaders(_headerBlock.ToArray());

        // RFC 8441 §4 / RFC 9220 — validate extended CONNECT before materializing
        // the request: the :protocol pseudo-header is only valid on a CONNECT, and
        // an extended CONNECT MUST also carry :scheme, :path, and :authority. A
        // violation is a malformed request, which RFC 9113 §8.1.1 treats as a
        // connection-level PROTOCOL_ERROR (GOAWAY). The cross-field rule is shared
        // with HTTP/3 via HttpFieldNormalization so both versions reject the same set.
        string? extendedConnectViolation = HttpFieldNormalization.ValidateExtendedConnect(
            decodedHeaders.Method,
            decodedHeaders.Scheme,
            decodedHeaders.Path,
            decodedHeaders.Authority,
            decodedHeaders.Protocol);
        if (extendedConnectViolation is not null)
        {
            throw new Http2ConnectionException(Http2ErrorCode.ProtocolError, extendedConnectViolation);
        }

        // RFC 9113 §5.2 — the body streams in through the flow-control-aware pipe
        // rather than being buffered whole before dispatch, so a large upload is
        // bounded by the advertised receive window and paced by the reader.
        Stream body = new Http2RequestBodyStream(_bodyChannel.Reader, onBodyConsumed, StreamId, requestAborted);
        HttpQueryCollection query = ParseQuery(decodedHeaders.Path ?? "/", out HttpPath path);
        // RFC 9113 §8.3.1 — :authority supersedes Host. Resolution is shared
        // across versions via HttpFieldNormalization so HTTP/2 and HTTP/3
        // reconcile authority identically.
        HttpHost host = HttpFieldNormalization.ResolveAuthority(decodedHeaders.Authority, decodedHeaders.Headers);
        HttpScheme scheme = decodedHeaders.Scheme is null
            ? fallbackScheme
            : string.Equals(decodedHeaders.Scheme, "https", StringComparison.OrdinalIgnoreCase) ? HttpScheme.Https : HttpScheme.Http;

        Http2Request request = new(
            host,
            path,
            HttpMethod.GetCanonicalizedValue(decodedHeaders.Method ?? HttpMethod.Get.Value),
            scheme,
            query,
            decodedHeaders.Headers,
            body);

        // RFC 9218 §4 / §8 — the request's Priority header initialises the
        // effective priority. Parsing is tolerant: a malformed header value is
        // ignored (the default urgency 3, non-incremental stands), and a
        // PRIORITY_UPDATE that already arrived for this stream is not overridden.
        if (decodedHeaders.Headers.TryGetValue(HttpHeaderKey.Priority, out HttpHeaderValue priorityValue)
            && HttpPriority.TryParse(priorityValue, out HttpPriority headerPriority))
        {
            SetPriorityFromHeader(headerPriority);
        }

        Http2Context context = new(this, request, new Http2Response(), connectionInfo, requestAborted);

        // Surface the :protocol pseudo-header (RFC 8441) generically so a
        // higher layer (the Assimalign.Cohesion.Http.ExtendedConnect package)
        // can model extended CONNECT without the transport knowing about it.
        if (decodedHeaders.Protocol is not null)
        {
            context.Items[Internal.TransportItemKeys.Protocol] = decodedHeaders.Protocol;
        }

        return context;
    }

    private static HttpQueryCollection ParseQuery(string requestTarget, out HttpPath path)
    {
        int queryIndex = requestTarget.IndexOf('?');

        if (queryIndex >= 0)
        {
            path = HttpPath.FromUriComponent(requestTarget[..queryIndex]);
            return new HttpQuery(requestTarget[(queryIndex + 1)..]).Parse();
        }

        path = HttpPath.FromUriComponent(requestTarget);
        return new HttpQueryCollection();
    }
}
