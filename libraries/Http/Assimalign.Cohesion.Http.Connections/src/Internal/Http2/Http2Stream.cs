using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;

using Assimalign.Cohesion.Http.Connections.Internal.Http2.HPack;

namespace Assimalign.Cohesion.Http.Connections.Internal.Http2;

/// <summary>
/// Server-side HTTP/2 stream — tracks the RFC 9113 §5.1 lifecycle state
/// plus the accumulating header block and body bytes received from the
/// peer.
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
/// </remarks>
internal sealed class Http2Stream
{
    private readonly MemoryStream _headerBlock;
    private readonly MemoryStream _body;
    // RFC 9113 §5.4.2 — when the peer resets a stream (or we decide to
    // reset it locally), the application MUST be able to learn that its
    // request was abandoned. This token source backs IHttpContext's
    // RequestAborted and is fired whenever the stream is reset.
    private readonly CancellationTokenSource _abortedSource = new();

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
    /// <c>SETTINGS_INITIAL_WINDOW_SIZE</c>.
    /// </summary>
    public Http2FlowControlWindow ReceiveWindow;

    public Http2Stream(int streamId, long initialSendWindow, long initialReceiveWindow)
    {
        StreamId = streamId;
        _headerBlock = new MemoryStream();
        _body = new MemoryStream();
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

    /// <summary>Current lifecycle state per RFC 9113 §5.1.</summary>
    public Http2StreamState State { get; private set; }

    /// <summary><see langword="true"/> once <c>END_HEADERS</c> has been observed across HEADERS/CONTINUATION.</summary>
    public bool HeadersCompleted { get; private set; }

    /// <summary><see langword="true"/> once the request side has been closed (END_STREAM from the peer or RST_STREAM).</summary>
    public bool InputCompleted { get; private set; }

    /// <summary>The stream is ready to materialise as an <c>IHttpContext</c> once both halves are present.</summary>
    public bool IsRequestReady => HeadersCompleted && InputCompleted && State != Http2StreamState.Closed;

    /// <summary>
    /// Whether the stream has reached the terminal <see cref="Http2StreamState.Closed"/>
    /// state.
    /// </summary>
    public bool IsClosed => State == Http2StreamState.Closed;

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

        AppendHeaderBytes(payload);

        if (endHeaders)
        {
            HeadersCompleted = true;
        }

        if (endStream)
        {
            InputCompleted = true;
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
        if (State == Http2StreamState.Idle || State == Http2StreamState.Closed)
        {
            throw new Http2ConnectionException(
                Http2ErrorCode.ProtocolError,
                $"HTTP/2 CONTINUATION frame received on stream {StreamId} in state {State}.");
        }

        AppendHeaderBytes(payload);

        if (endHeaders)
        {
            HeadersCompleted = true;
        }
    }

    /// <summary>
    /// Folds an inbound DATA payload into the accumulating body and applies the
    /// END_STREAM transition.
    /// </summary>
    /// <exception cref="Http2StreamException">
    /// Thrown when DATA is received on a stream that has already had its remote
    /// half closed (STREAM_CLOSED — RFC 9113 §5.1).
    /// </exception>
    /// <exception cref="Http2ConnectionException">
    /// Thrown when DATA is received on a stream in <see cref="Http2StreamState.Idle"/>
    /// (no HEADERS yet) — that is a PROTOCOL_ERROR connection-level fault.
    /// </exception>
    public void ReceiveData(ReadOnlySpan<byte> payload, bool endStream)
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

        if (!payload.IsEmpty)
        {
            _body.Write(payload);
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
        if (State == Http2StreamState.Idle)
        {
            throw new Http2ConnectionException(
                Http2ErrorCode.ProtocolError,
                $"HTTP/2 RST_STREAM received on idle stream {StreamId}.");
        }

        State = Http2StreamState.Closed;
        InputCompleted = true;
        TryFireAbort();
    }

    /// <summary>
    /// Marks the stream's local half as closed because the server is about
    /// to send a frame carrying END_STREAM (the typical case is the response
    /// body's terminating DATA frame, or a header-only response).
    /// </summary>
    public void SendEndStream()
    {
        State = State switch
        {
            Http2StreamState.Open => Http2StreamState.HalfClosedLocal,
            Http2StreamState.HalfClosedRemote => Http2StreamState.Closed,
            // Idempotent / no-op for already-closed states.
            _ => State,
        };
    }

    /// <summary>
    /// Marks the stream as closed because the server emitted a RST_STREAM,
    /// and fires <see cref="RequestAborted"/> so the application sees
    /// the cancellation.
    /// </summary>
    public void SendReset()
    {
        State = Http2StreamState.Closed;
        InputCompleted = true;
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
    }

    private void AppendHeaderBytes(ReadOnlySpan<byte> payload)
    {
        if (!payload.IsEmpty)
        {
            _headerBlock.Write(payload);
        }
    }

    public Http2Context CreateContext(HPackDecoder decoder, HttpConnectionInfo connectionInfo, HttpScheme fallbackScheme, CancellationToken connectionAborted)
    {
        if (!IsRequestReady)
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
        byte[] bodyBytes = _body.ToArray();
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
            new MemoryStream(bodyBytes, writable: false));

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
