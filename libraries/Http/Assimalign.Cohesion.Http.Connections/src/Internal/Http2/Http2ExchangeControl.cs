using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Http.Connections.Internal;

/// <summary>
/// HTTP/2 <see cref="IHttpExchangeControl"/> — the per-exchange control surface offered to
/// response interceptors through <see cref="HttpExchangeInterceptorResponseContext.Control"/>. Interim
/// (<c>1xx</c>) responses are emitted as additional HEADERS blocks on the exchange's stream ahead
/// of the final response (RFC 9113 §8.1), delegated to
/// <see cref="Http2ConnectionContext.WriteInterimResponseAsync"/> which holds the connection write
/// gate so the interim HEADERS never interleave with concurrent frames. Takeover is unsupported —
/// HTTP/2 exchanges are multiplexed streams over a shared connection and the protocol removed the
/// <c>Upgrade</c> mechanism (RFC 9113 §8.6); its per-stream counterpart, the extended CONNECT tunnel
/// (RFC 8441), is <see cref="AcceptTunnelAsync"/>, which claims the stream's final response and
/// writes the <c>200</c> HEADERS block without <c>END_STREAM</c> through
/// <see cref="Http2ConnectionContext.WriteTunnelHeadAsync"/>, under the connection write gate.
/// Aborting is not a control mechanism — it is the application-owned
/// <see cref="IHttpContext.Cancel"/>, which the send path honors by resetting the single stream
/// (<c>RST_STREAM(CANCEL)</c>), leaving the connection's other streams intact.
/// </summary>
internal sealed class Http2ExchangeControl : IHttpExchangeControl
{
    private readonly Http2ConnectionContext _connection;
    private readonly Http2Context _context;
    private readonly bool _isExtendedConnect;
    private int _tunnelAcceptCalled;

    public Http2ExchangeControl(Http2ConnectionContext connection, Http2Context context)
    {
        _connection = connection;
        _context = context;
        // RFC 8441 §4 — defense in depth over the head validation: only a CONNECT that carries a
        // non-empty :protocol may become a tunnel. Captured at dispatch, before the application can
        // see the request or rewrite its method.
        _isExtendedConnect = HttpFieldNormalization.IsExtendedConnect(context.Request.Method.Value, context.ExtendedConnectProtocol);
    }

    /// <inheritdoc />
    /// <remarks>
    /// Also <see langword="true"/> once the transport has answered the stream itself — a request body
    /// that crossed its cap is answered <c>413</c> by the frame pump, and one that fell below the minimum
    /// data rate <c>408</c> by the body reader — since that is the stream's final response.
    /// </remarks>
    public bool HasResponseStarted => _context.HasFinalResponseStarted || _context.Stream.IsResponseClaimed;

    /// <inheritdoc />
    public bool CanWriteInterimResponse => !HasResponseStarted && !_context.CancelRequested;

    /// <inheritdoc />
    public async ValueTask WriteInterimResponseAsync(
        HttpStatusCode statusCode,
        IHttpHeaderCollection? headers = null,
        CancellationToken cancellationToken = default)
    {
        HttpInterimResponseRules.ValidateInterimStatusCode(statusCode);

        if (!CanWriteInterimResponse)
        {
            throw new InvalidOperationException(
                "An interim response cannot be sent after the final HTTP/2 response has started.");
        }

        await _connection.WriteInterimResponseAsync(_context, statusCode, headers, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public bool CanTakeOver => false;

    /// <inheritdoc />
    public Stream TakeOver()
    {
        throw new InvalidOperationException(
            "HTTP/2 exchanges are multiplexed streams over a shared connection and cannot be taken over (RFC 9113 §8.6).");
    }

    /// <inheritdoc />
    public bool CanAcceptTunnel =>
        _isExtendedConnect
        && Volatile.Read(ref _tunnelAcceptCalled) == 0
        && !_context.CancelRequested
        && !HasResponseStarted;

    /// <inheritdoc />
    /// <remarks>
    /// The guards run in the order <see cref="HttpExtendedConnectRules"/> records. The response counts
    /// as started once the stream's final response is claimed, too — by the transport's own
    /// <c>413</c> or <c>408</c>, which a CONNECT never draws since its octets are not a message body
    /// (neither the body-size cap nor the minimum data rate applies to them), but the claim rules it out
    /// regardless.
    /// </remarks>
    public async ValueTask<Stream> AcceptTunnelAsync(CancellationToken cancellationToken = default)
    {
        if (!_isExtendedConnect || _context.RequestBody is not { } requestBody)
        {
            throw new InvalidOperationException(HttpExtendedConnectRules.NotExtendedConnectMessage);
        }

        if (Interlocked.Exchange(ref _tunnelAcceptCalled, 1) == 1)
        {
            throw new InvalidOperationException(HttpExtendedConnectRules.AlreadyAcceptedMessage);
        }

        if (_context.CancelRequested)
        {
            throw new InvalidOperationException(HttpExtendedConnectRules.CancelledMessage);
        }

        if (HasResponseStarted)
        {
            throw new InvalidOperationException(HttpExtendedConnectRules.ResponseStartedMessage);
        }

        if (_context.Stream.IsReset)
        {
            throw new IOException($"The HTTP/2 stream {_context.StreamId} was reset before the extended CONNECT tunnel was accepted.");
        }

        HttpStatusCode stagedStatus = _context.Response.StatusCode;
        HttpExtendedConnectRules.PrepareResponseHead(_context.Response);

        // Encoded before the response is claimed: a field the head cannot carry (#1183) throws with
        // nothing on the wire and the exchange unstarted, its staged status restored. The accept is
        // spent, and the exchange is answered like any other.
        byte[] headerBlock;

        try
        {
            headerBlock = HPackEncoder.EncodeResponseHeaders(_context.Response.StatusCode, _context.Response.Headers);
        }
        catch (HttpInvalidResponseFieldException)
        {
            _context.Response.StatusCode = stagedStatus;
            throw;
        }

        // RFC 9113 §8.1 — the stream carries one final response, and from here it is the tunnel's.
        if (!_context.Stream.TryClaimResponse())
        {
            throw new InvalidOperationException(HttpExtendedConnectRules.ResponseStartedMessage);
        }

        _context.MarkFinalResponseStarted();

        Http2ExtendedConnectStream tunnel = new(_connection, _context, requestBody);
        _context.Tunnel = tunnel;

        await _connection.WriteTunnelHeadAsync(_context, headerBlock, cancellationToken).ConfigureAwait(false);
        tunnel.MarkHeadCommitted();

        return tunnel;
    }
}
