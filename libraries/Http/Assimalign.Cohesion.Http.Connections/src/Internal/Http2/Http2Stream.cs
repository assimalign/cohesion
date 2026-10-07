using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;


namespace Assimalign.Cohesion.Http.Connections.Internal;

/// <summary>
/// Server-side HTTP/2 stream — tracks the RFC 9113 §5.1 lifecycle state
/// plus the accumulating header block (the request head, then any trailer
/// section) and the streaming body pipe fed from the peer's DATA frames.
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
    // _responseOwner values. RFC 9113 §8.1 — a stream carries exactly one final response, written
    // either by the application or, when the transport rejects the request itself (413), by the
    // transport.
    private const int responseOwnerNone = 0;
    private const int responseOwnerApplication = 1;
    private const int responseOwnerTransport = 2;

    private readonly MemoryStream _headerBlock;
    // RFC 9113 §6.10 / §10.5.1 — cap the raw header-block bytes accumulated across a HEADERS frame
    // and its CONTINUATION frames. Without this bound a CONTINUATION flood — an endless run of
    // CONTINUATION frames with no END_HEADERS — grows this buffer without limit. The cap is the
    // advertised SETTINGS_MAX_HEADER_LIST_SIZE; exceeding it is a connection-level ENHANCE_YOUR_CALM.
    private readonly int _maxHeaderBlockSize;
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

    // RFC 9113 §6.8 — graceful-close drain accounting, a tri-state latch:
    // 0 = this stream was never counted as an in-flight exchange;
    // 1 = counted (the pump dispatched its context and incremented the
    //     connection's active-exchange count);
    // 2 = accounted complete (exactly one completion path — response sent,
    //     stream reset, or a truncated-input shutdown abort — claimed the
    //     decrement). Interlocked because the pump, the request handler, and
    //     the graceful-close path race for the transitions.
    private int _exchangeAccounting;

    // RFC 9113 §5.4.2 — set once the stream has been reset in either direction (RST_STREAM received
    // from the peer, or emitted locally). No further frame may be sent on a reset stream, so response
    // writers read this to abandon an in-flight response, and a writer parked on send-window credit
    // is woken to observe it. Written under _stateLock, read lock-free by the writer threads.
    private volatile bool _reset;

    // Who owns this stream's final response (the responseOwner* constants): the application claims
    // it when its buffered send or streaming head commit starts the final response; the transport
    // claims it only to answer a request it rejects itself. The frame pump and the application race
    // for the claim, so it is taken with Interlocked.
    private int _responseOwner;

    // Set once the END_STREAM completing the application's final response is on the wire.
    private volatile bool _responseCompleted;

    // RFC 9110 §15.5.14 — the effective request-body cap frozen at dispatch (null = unbounded; always
    // null for CONNECT, whose post-head octets are tunnel traffic, not a message body), the running
    // total of de-padded DATA octets received, and whether the cap has been crossed. Pump-only state:
    // the frame pump is the single writer and the single reader.
    private long? _maxRequestBodySize;
    private long _requestBodyReceived;
    private bool _requestBodyRejected;

    // RFC 9113 §8.1 — the request's trailer section: a second field block, opened by a HEADERS frame
    // that carries END_STREAM once the head is in, and closed by END_HEADERS. It accumulates in
    // _headerBlock, which the head's decode emptied, and the frame pump decodes it as soon as it is
    // complete (ReceiveTrailers). Pump-only state.
    private bool _receivingTrailers;
    private bool _trailerBlockCompleted;
    // Whether the request is a CONNECT, whose stream carries only DATA after the head (RFC 9113 §8.5).
    // Set at dispatch; pump-only.
    private bool _isConnect;
    // The validated trailer fields. The pump writes them before it completes the body pipe; the body
    // reader reads them after it observes that completion, and copies them into _requestTrailers.
    private HttpHeaderCollection? _receivedTrailers;
    // The request's trailer collection, created at dispatch and filled when the body is read to its end.
    private HttpTrailerCollection? _requestTrailers;

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

    public Http2Stream(int streamId, long initialSendWindow, long initialReceiveWindow, int maxHeaderBlockSize)
    {
        StreamId = streamId;
        InitialReceiveWindow = initialReceiveWindow;
        _headerBlock = new MemoryStream();
        _maxHeaderBlockSize = maxHeaderBlockSize;
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
    /// Whether the request's trailer section has been received in full (END_HEADERS) and waits to be
    /// decoded by <see cref="ReceiveTrailers"/>.
    /// </summary>
    public bool IsTrailerBlockReady => _receivingTrailers && _trailerBlockCompleted;

    /// <summary>
    /// Marks this stream as counted toward the connection's in-flight exchange
    /// total for the RFC 9113 §6.8 graceful-close drain. Called by the pump,
    /// paired with the connection-level increment, immediately before the
    /// request context is handed to the consumer.
    /// </summary>
    public void MarkExchangeCounted()
    {
        Interlocked.CompareExchange(ref _exchangeAccounting, 1, 0);
    }

    /// <summary>
    /// Atomically claims the single "exchange complete" accounting slot for this
    /// stream. Returns <see langword="true"/> only for the first caller — and only
    /// when the stream was previously counted via <see cref="MarkExchangeCounted"/>
    /// — so the connection decrements its in-flight exchange count at most once
    /// per counted stream and never for a stream that was refused, reset, or torn
    /// down before dispatch.
    /// </summary>
    /// <returns><see langword="true"/> if the caller won the accounting slot.</returns>
    public bool TryClaimExchangeAccounting()
    {
        return Interlocked.CompareExchange(ref _exchangeAccounting, 2, 1) == 1;
    }

    /// <summary>
    /// Whether the stream has been reset in either direction — an inbound <c>RST_STREAM</c> or one
    /// the server emitted. RFC 9113 §5.4.2: no further frame may be sent for a reset stream, so a
    /// response writer that observes this abandons whatever it has not yet written.
    /// </summary>
    public bool IsReset => _reset;

    /// <summary>
    /// Whether the request head declared a <c>content-length</c> larger than the stream's frozen
    /// request-body cap. Decided when the context is created, so the transport can reject the request
    /// with <c>413</c> before a single body octet is read (RFC 9110 §15.5.14).
    /// </summary>
    public bool IsDeclaredBodyOverLimit { get; private set; }

    /// <summary>
    /// Whether the stream's final response has been claimed, by the application or by the transport.
    /// Once it has, an interim (<c>1xx</c>) response can no longer precede it.
    /// </summary>
    public bool IsResponseClaimed => Volatile.Read(ref _responseOwner) != responseOwnerNone;

    /// <summary>
    /// Whether the transport claimed the stream's final response to answer a request it rejected
    /// itself (<c>413 Content Too Large</c>). The application's response for such an exchange is never
    /// written.
    /// </summary>
    public bool IsAnsweredByTransport => Volatile.Read(ref _responseOwner) == responseOwnerTransport;

    /// <summary>
    /// Whether the <c>END_STREAM</c> completing the application's final response has been written.
    /// </summary>
    public bool IsResponseCompleted => _responseCompleted;

    /// <summary>
    /// Whether the application may still write frames for its final response: the application owns
    /// the response, has not completed it, and the stream has not been reset.
    /// </summary>
    public bool CanWriteResponse =>
        !_reset && !_responseCompleted && Volatile.Read(ref _responseOwner) == responseOwnerApplication;

    /// <summary>
    /// Claims the stream's final response for the application — called by the buffered send path and
    /// by the streaming head commit immediately before the final response's HEADERS are written.
    /// </summary>
    /// <returns>
    /// <see langword="true"/> when the application now owns the final response;
    /// <see langword="false"/> when the transport already answered the stream itself (a rejected
    /// request body), in which case that answer stands and the application writes nothing.
    /// </returns>
    public bool TryClaimResponse()
    {
        return Interlocked.CompareExchange(ref _responseOwner, responseOwnerApplication, responseOwnerNone) == responseOwnerNone;
    }

    /// <summary>
    /// Claims the stream's final response for the transport, to answer a request it rejects itself
    /// (<c>413 Content Too Large</c>).
    /// </summary>
    /// <returns>
    /// <see langword="true"/> when the transport now owns the final response;
    /// <see langword="false"/> when the application already started its own.
    /// </returns>
    public bool TryClaimResponseForRejection()
    {
        return Interlocked.CompareExchange(ref _responseOwner, responseOwnerTransport, responseOwnerNone) == responseOwnerNone;
    }

    /// <summary>
    /// Records that the <c>END_STREAM</c> completing the application's final response is on the wire.
    /// </summary>
    public void CompleteResponse()
    {
        _responseCompleted = true;
    }

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
    /// <remarks>
    /// A HEADERS frame that arrives once the head is in opens the trailer section (RFC 9113 §8.1).
    /// Its block is decoded on its own once complete (<see cref="ReceiveTrailers"/>), and the request
    /// body ends only then, so a reader that reaches the end of the body finds the section validated.
    /// </remarks>
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
        bool opensTrailers = false;

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
                    opensTrailers = true;
                    break;
                case Http2StreamState.Closed when _reset:
                    // RFC 9113 §5.1 — the server reset this stream between the pump finding it and
                    // this frame (a peer's reset removes the stream before any later frame is read).
                    // The peer sent the frame before it saw the reset: its block is still decoded
                    // (RFC 9113 §4.3), and ReceiveTrailers then ignores it.
                    opensTrailers = true;
                    break;
                case Http2StreamState.HalfClosedRemote:
                case Http2StreamState.Closed:
                    throw new Http2ConnectionException(
                        Http2ErrorCode.StreamClosed,
                        $"HTTP/2 HEADERS frame received on stream {StreamId} in state {State}; the peer has already closed its half.");
            }
        }

        if (opensTrailers)
        {
            _receivingTrailers = true;
        }

        AppendHeaderBytes(payload);

        if (endHeaders)
        {
            CompleteHeaderBlock();
        }

        // The trailer section ends the input when ReceiveTrailers has decoded it, not here.
        if (endStream && !opensTrailers)
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
        // assert that the stream itself is in a sane state. A trailing HEADERS
        // frame closes a stream whose response is already complete, and the
        // CONTINUATION frames of that trailer section still belong to it.
        lock (_stateLock)
        {
            if (State == Http2StreamState.Idle || (State == Http2StreamState.Closed && !_receivingTrailers))
            {
                throw new Http2ConnectionException(
                    Http2ErrorCode.ProtocolError,
                    $"HTTP/2 CONTINUATION frame received on stream {StreamId} in state {State}.");
            }
        }

        AppendHeaderBytes(payload);

        if (endHeaders)
        {
            CompleteHeaderBlock();
        }
    }

    /// <summary>
    /// Decodes the completed trailer section with the connection's HPACK decoder, validates it, and
    /// ends the request input. The frame pump calls this as soon as END_HEADERS completes the section,
    /// in frame order and whether or not the application ever reads it: HPACK is stateful, so every
    /// field block must be decoded or the decoder falls out of step for every later request on the
    /// connection (RFC 9113 §4.3).
    /// </summary>
    /// <remarks>
    /// The whole block is decoded before any field is judged, so a malformed section still leaves the
    /// decoder in step and costs only this stream. A valid section is published to the request's
    /// trailer collection when the body is read to its end (<see cref="PublishTrailers"/>). A section
    /// that was still arriving when the server reset the stream is decoded and otherwise ignored
    /// (RFC 9113 §5.1).
    /// </remarks>
    /// <param name="decoder">The connection's HPACK decoder.</param>
    /// <exception cref="HPackDecodingException">
    /// The block is not valid HPACK, or its decoded list exceeds the advertised
    /// <c>SETTINGS_MAX_HEADER_LIST_SIZE</c>. The caller maps it to a connection error.
    /// </exception>
    /// <exception cref="Http2StreamException">
    /// The section is malformed (RFC 9113 §8.1.1) — a pseudo-header field, a connection-specific field,
    /// a field prohibited in trailers, an uppercase name, or any trailer section after a CONNECT head
    /// (RFC 9113 §8.5): a stream error of type <c>PROTOCOL_ERROR</c>. The body pipe fails first, so the
    /// reader never mistakes the request for a complete one.
    /// </exception>
    public void ReceiveTrailers(HPackDecoder decoder)
    {
        _receivingTrailers = false;

        List<(string Name, string Value)> fields = decoder.DecodeFieldLines(
            new ReadOnlySpan<byte>(_headerBlock.GetBuffer(), 0, (int)_headerBlock.Length));
        _headerBlock.SetLength(0);

        // RFC 9113 §5.1 — the server reset the stream while the section was arriving. Decoding it was
        // the only work left; nobody reads the request any more, so it is neither judged nor published.
        if (_reset)
        {
            return;
        }

        HttpHeaderCollection trailers = new();
        InputCompleted = true;

        try
        {
            if (_isConnect)
            {
                throw new InvalidDataException(
                    $"HTTP/2 stream {StreamId} carried a trailer section after a CONNECT head; a CONNECT stream carries only DATA (RFC 9113 §8.5).");
            }

            HttpTrailerFieldRules.AddReceivedFields(fields, trailers, "HTTP/2");
        }
        catch (InvalidDataException exception)
        {
            FailBody(new IOException(exception.Message, exception));
            throw new Http2StreamException(StreamId, Http2ErrorCode.ProtocolError, exception.Message);
        }

        Volatile.Write(ref _receivedTrailers, trailers);
        CompleteBody();
    }

    private void CompleteHeaderBlock()
    {
        if (_receivingTrailers)
        {
            _trailerBlockCompleted = true;
        }
        else
        {
            HeadersCompleted = true;
        }
    }

    /// <summary>
    /// Copies the validated trailer section into the request's trailer collection. The request body
    /// calls this once, when its reader reaches the clean end of the body — where HTTP/1.1 and HTTP/3
    /// surface trailers too — so the collection is filled on the reader's own thread, after the frame
    /// pump completed the body pipe.
    /// </summary>
    private void PublishTrailers()
    {
        if (Volatile.Read(ref _receivedTrailers) is not { } received || _requestTrailers is null)
        {
            return;
        }

        foreach (KeyValuePair<HttpHeaderKey, HttpHeaderValue> field in received)
        {
            _requestTrailers[field.Key] = field.Value;
        }
    }

    /// <summary>
    /// Queues an inbound DATA payload onto the body pipe and applies the END_STREAM
    /// transition, enforcing the stream's request-body cap on receipt.
    /// </summary>
    /// <param name="data">The de-padded application data.</param>
    /// <param name="flowControlLength">
    /// The DATA frame's full payload length (including padding) — the octets to
    /// credit back to the peer once the application consumes this chunk.
    /// </param>
    /// <param name="endStream">Whether the frame carried END_STREAM.</param>
    /// <returns>
    /// <see langword="true"/> when the payload was delivered to the body pipe;
    /// <see langword="false"/> when the request content has crossed the stream's body-size cap
    /// (RFC 9110 §15.5.14). The crossing frame, and any later one, is not delivered: the body pipe is
    /// failed instead, so the reader drains the octets received below the cap and then observes an
    /// <see cref="IOException"/>. The caller answers the violation on the wire. The frame's
    /// flow-control cost stays consumed; the caller's stream removal reclaims it.
    /// </returns>
    /// <exception cref="Http2StreamException">
    /// Thrown when DATA is received on a stream that has already had its remote
    /// half closed (STREAM_CLOSED — RFC 9113 §5.1).
    /// </exception>
    /// <exception cref="Http2ConnectionException">
    /// Thrown when DATA is received on a stream in <see cref="Http2StreamState.Idle"/>
    /// (no HEADERS yet) — that is a PROTOCOL_ERROR connection-level fault.
    /// </exception>
    public bool ReceiveData(ReadOnlyMemory<byte> data, int flowControlLength, bool endStream)
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

        // RFC 9110 §15.5.14 — the request content is capped on receipt, against the de-padded octets
        // (padding is framing, not content). Enforcing here rather than on the reader's pace means the
        // cap bounds what the peer may push even when the handler never reads the body.
        if (_requestBodyRejected)
        {
            return false;
        }

        if (_maxRequestBodySize is { } limit)
        {
            _requestBodyReceived += data.Length;

            if (_requestBodyReceived > limit)
            {
                _requestBodyRejected = true;
                FailBody(new IOException(
                    $"The HTTP/2 request body on stream {StreamId} exceeded the maximum request body size of {limit} octets (413 Content Too Large)."));
                return false;
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

        return true;
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
            _reset = true;
        }

        CutOffBody($"The HTTP/2 stream {StreamId} was reset before its request body ended.");
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
            _reset = true;
        }

        CutOffBody($"The HTTP/2 stream {StreamId} was reset before its request body ended.");
    }

    /// <summary>
    /// Fires <see cref="RequestAborted"/> for an exchange the transport ended without a reset — its
    /// own <c>413</c> completed a stream the peer had already half-closed — so a handler still running
    /// learns its request is over. Idempotent.
    /// </summary>
    public void AbortRequest()
    {
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
    /// <see cref="RequestAborted"/> and fails the body pipe so a handler parked
    /// reading the body observes the abort — not a clean end-of-stream, which
    /// would let it mistake a truncated body for a complete one.
    /// </summary>
    /// <returns>
    /// <see langword="true"/> when the request was actually aborted (its input was
    /// still incoming — the exchange can no longer complete normally, so the
    /// graceful-close drain should stop waiting on it); <see langword="false"/>
    /// when the fully-received request remains answerable and the drain should
    /// keep waiting for its response.
    /// </returns>
    public bool AbortOnShutdown()
    {
        // Only a body that was still incoming is truncated. A fully-received body
        // (END_STREAM already observed) is complete and buffered; the handler must
        // still be able to read it, so do NOT fire the abort for it.
        if (!InputCompleted)
        {
            CutOffBody($"The HTTP/2 connection closed before the request body on stream {StreamId} ended.");
            return true;
        }

        CompleteBody();
        return false;
    }

    /// <summary>
    /// Aborts the exchange because the host stopped waiting for the connection — it cancelled the
    /// receive enumeration, or the graceful close's bounded drain ran out. Unlike
    /// <see cref="AbortOnShutdown"/>, a fully received request is aborted too, since nothing waits for
    /// its response any longer: fires <see cref="RequestAborted"/> and fails a body pipe that had not
    /// ended. Idempotent.
    /// </summary>
    public void AbortOnCancellation()
    {
        CutOffBody($"The HTTP/2 exchange on stream {StreamId} was abandoned before its request body ended.");
    }

    /// <summary>
    /// Ends a request whose body was cut off before its END_STREAM (RFC 9113 §8.1): fires the abort
    /// first, then fails the body pipe with an <see cref="IOException"/> instead of completing it. A
    /// reader that wakes on the pipe therefore never reads a clean end of the body, whichever signal
    /// reaches it first, and the trailer section is never published (#1327). A body that already
    /// ended keeps its clean end: the pipe's one-shot latch makes the failure a no-op.
    /// </summary>
    /// <param name="reason">Why the body was cut off.</param>
    private void CutOffBody(string reason)
    {
        TryFireAbort();
        FailBody(new IOException(reason));
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
    /// Completes the body pipe with <paramref name="error"/>: the reader still drains every chunk
    /// already delivered, then its next read throws <paramref name="error"/>. Shares
    /// <see cref="CompleteBody"/>'s one-shot latch, so a body that already completed is unaffected.
    /// </summary>
    private void FailBody(Exception error)
    {
        if (Interlocked.Exchange(ref _bodyCompleted, 1) == 0)
        {
            _bodyChannel.Writer.TryComplete(error);
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
        if (payload.IsEmpty)
        {
            return;
        }

        // RFC 9113 §6.10 / §10.5.1 — reject a header block whose accumulated raw bytes exceed the
        // advertised SETTINGS_MAX_HEADER_LIST_SIZE. This is the CONTINUATION-flood defence: it trips
        // before decode, so an attacker cannot pin memory by streaming CONTINUATION frames that
        // never carry END_HEADERS. ENHANCE_YOUR_CALM signals excessive load (RFC 9113 §7).
        if (_headerBlock.Length + payload.Length > _maxHeaderBlockSize)
        {
            throw new Http2ConnectionException(
                Http2ErrorCode.EnhanceYourCalm,
                $"HTTP/2 header block on stream {StreamId} exceeded the maximum of {_maxHeaderBlockSize} octets (CONTINUATION flood).");
        }

        _headerBlock.Write(payload);
    }

    /// <summary>
    /// Materializes the completed request head as an <see cref="Http2Context"/>, running the
    /// registered request-parse interceptors (head + body hooks) as the head is assembled — the
    /// HTTP/2 analogue of the HTTP/1.1 <c>Http1MessageReader</c> invocation point. Head hooks run
    /// before the application observes any body octet (the body streams in through the
    /// flow-control-bounded pipe afterward); body hooks wrap the streaming body stream.
    /// </summary>
    /// <param name="decoder">The connection's HPACK decoder.</param>
    /// <param name="connectionInfo">The transport endpoints for the exchange.</param>
    /// <param name="fallbackScheme">The connection scheme used when the request omits <c>:scheme</c>.</param>
    /// <param name="connectionAborted">The connection-teardown token linked into the request's abort token.</param>
    /// <param name="onBodyConsumed">The consume callback crediting body flow-control cost back to the peer.</param>
    /// <param name="interceptors">The listener's snapshotted request-parse interceptors.</param>
    /// <param name="maxRequestBodySize">The registration's body-size cap seeded into the parse context.</param>
    /// <returns>The materialized request context, with any hook-attached features flowed in.</returns>
    /// <exception cref="HttpRequestRejectedException">
    /// Thrown when a request-parse interceptor rejects the request.
    /// </exception>
    /// <exception cref="Http2StreamException">
    /// Thrown with <see cref="Http2ErrorCode.ProtocolError"/> when the <c>:path</c> does not decode to a
    /// legal path — a malformed request, reset per stream (RFC 9113 §8.1.1).
    /// </exception>
    public async ValueTask<Http2Context> CreateContextAsync(
        HPackDecoder decoder,
        HttpConnectionInfo connectionInfo,
        HttpScheme fallbackScheme,
        CancellationToken connectionAborted,
        Func<int, int, CancellationToken, ValueTask> onBodyConsumed,
        IHttpExchangeInterceptor[] interceptors,
        long? maxRequestBodySize)
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

        // A trailer section that follows the body is a field block of its own: it accumulates from an
        // empty block, under its own size bound (ReceiveHeaders, ReceiveTrailers).
        _headerBlock.SetLength(0);

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

        // RFC 3986 §2.4 — the :path is percent-decoded through the same HttpPath.FromUriComponent
        // decode HTTP/1.1 and HTTP/3 use. A :path whose decoded form is not a legal path — a decoded
        // space, control character, '?', '#', or NUL, or no leading '/' — makes the request malformed,
        // which RFC 9113 §8.1.1 / §8.3.1 require to be a stream error of type PROTOCOL_ERROR. The
        // header block has been fully decoded, so the connection's HPACK state is intact: only this
        // stream is reset and the connection keeps serving its other streams. The decode semantics
        // themselves are unchanged (h1/h2/h3 parity); only the failure's scope is.
        HttpQueryCollection query;
        HttpPath path;
        try
        {
            query = ParseQuery(decodedHeaders.Path ?? "/", out path);
        }
        catch (Exception exception) when (exception is HttpException or InvalidOperationException)
        {
            throw new Http2StreamException(
                StreamId,
                Http2ErrorCode.ProtocolError,
                $"HTTP/2 stream {StreamId} carried a malformed :path: {exception.Message}");
        }

        // RFC 9113 §5.2 — the body streams in through the flow-control-aware pipe
        // rather than being buffered whole before dispatch, so a large upload is
        // bounded by the advertised receive window and paced by the reader. RFC 9110
        // §6.5 — the trailer collection is supported and starts empty; the body fills
        // it when its reader reaches the end (PublishTrailers).
        _requestTrailers = new HttpTrailerCollection(isSupported: true);
        Http2RequestBodyStream body = new(_bodyChannel.Reader, onBodyConsumed, StreamId, requestAborted, PublishTrailers);
        // RFC 9113 §8.3.1 — :authority supersedes Host. Resolution is shared
        // across versions via HttpFieldNormalization so HTTP/2 and HTTP/3
        // reconcile authority identically.
        HttpHost host = HttpFieldNormalization.ResolveAuthority(decodedHeaders.Authority, decodedHeaders.Headers);
        HttpScheme scheme = decodedHeaders.Scheme is null
            ? fallbackScheme
            : string.Equals(decodedHeaders.Scheme, "https", StringComparison.OrdinalIgnoreCase) ? HttpScheme.Https : HttpScheme.Http;

        HttpMethod method = HttpMethod.GetCanonicalizedValue(decodedHeaders.Method ?? HttpMethod.Get.Value);
        TransportHttpRequestHead requestHead = new(
            host,
            path,
            method,
            scheme,
            query,
            decodedHeaders.Headers,
            body,
            _requestTrailers);

        // RFC 9218 §4 / §8 — the request's Priority header initialises the
        // effective priority. Parsing is tolerant: a malformed header value is
        // ignored (the default urgency 3, non-incremental stands), and a
        // PRIORITY_UPDATE that already arrived for this stream is not overridden.
        if (decodedHeaders.Headers.TryGetValue(HttpHeaderKey.Priority, out HttpHeaderValue priorityValue)
            && HttpPriority.TryParse(priorityValue, out HttpPriority headerPriority))
        {
            SetPriorityFromHeader(headerPriority);
        }

        // Request-parse interceptor phase. RFC 9110 §9.3.6 — a CONNECT's post-head octets are
        // tunnel traffic rather than a message body, so body hooks are skipped for it (head hooks
        // still run). The hook-populated feature collection and the (possibly wrapped) body flow
        // into the exchange through the Http2Context constructor; zero interceptors keeps the
        // pre-seam fast path.
        bool isConnect = method == HttpMethod.Connect;
        _isConnect = isConnect;
        HttpRequestInterceptionResult interception = await HttpRequestInterceptorPipeline.InterceptAsync(
            interceptors,
            HttpVersion.Http20,
            requestHead,
            connectionInfo,
            maxRequestBodySize,
            isConnect).ConfigureAwait(false);

        // RFC 9110 §15.5.14 — arm the request-body cap. The pipeline froze the knob after the head
        // hooks, so the value is final for the exchange (an IHttpMaxRequestBodySizeFeature is
        // read-only from dispatch on HTTP/2). ReceiveData enforces it on receipt; a declared
        // content-length over it is flagged here so the connection rejects the request before a
        // single body octet is read. A CONNECT tunnel carries no message body, so it is uncapped.
        _maxRequestBodySize = isConnect ? null : interception.MaxRequestBodySize;
        IsDeclaredBodyOverLimit = _maxRequestBodySize is { } limit
            && TryGetDeclaredContentLength(decodedHeaders.Headers, out long declaredLength)
            && declaredLength > limit;

        // RFC 8441 §4 — :protocol is non-null only on a valid extended CONNECT (validated above). The
        // connection installs the extended CONNECT feature from it at dispatch; an accepted tunnel
        // reads the peer's DATA from the transport's own body stream.
        Http2Context context = new(
            this,
            requestHead with { Body = interception.Body },
            connectionInfo,
            requestAborted,
            interception.Features)
        {
            ExtendedConnectProtocol = decodedHeaders.Protocol,
            RequestBody = body,
            AddedResponseInterceptors = interception.ResponseInterceptors,
        };

        return context;
    }

    /// <summary>
    /// Reads a single, well-formed <c>content-length</c> value (RFC 9110 §8.6: a non-negative decimal
    /// integer). An absent, repeated, or unparsable field yields <see langword="false"/>: the
    /// declaration is then simply not used for the early rejection, and the receipt-side running total
    /// still enforces the cap.
    /// </summary>
    private static bool TryGetDeclaredContentLength(HttpHeaderCollection headers, out long contentLength)
    {
        contentLength = 0;

        return headers.TryGetValue(HttpHeaderKey.ContentLength, out HttpHeaderValue value)
            && value.Count == 1
            && long.TryParse(value.Value, NumberStyles.None, CultureInfo.InvariantCulture, out contentLength);
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
