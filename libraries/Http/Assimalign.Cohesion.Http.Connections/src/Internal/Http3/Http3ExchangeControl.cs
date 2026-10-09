using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Http.Connections.Internal;

/// <summary>
/// HTTP/3 <see cref="IHttpExchangeControl"/> — the per-exchange control surface offered to
/// response interceptors through <see cref="HttpExchangeInterceptorResponseContext.Control"/>. Interim
/// (<c>1xx</c>) responses are emitted as additional QPACK-encoded HEADERS frames on the request
/// stream ahead of the final HEADERS frame (RFC 9114 §4.1), delegated to
/// <see cref="Http3ConnectionContext.WriteInterimResponseAsync"/>. Takeover is unsupported —
/// HTTP/3 exchanges are multiplexed QUIC streams over a shared connection (RFC 9114 §4.2); their
/// per-stream counterpart, the extended CONNECT tunnel (RFC 9220), is
/// <see cref="AcceptTunnelAsync"/>, which writes the <c>200</c> HEADERS frame on the request stream
/// without ending it, through the tunnel's own serialized writer
/// (<see cref="Http3ExtendedConnectStream"/>). Aborting is not a control mechanism — it is the
/// application-owned <see cref="IHttpContext.Cancel"/>, which the send path honors by resetting the
/// single request stream, leaving the QUIC connection's other streams intact.
/// </summary>
internal sealed class Http3ExchangeControl : IHttpExchangeControl
{
    private readonly Http3ConnectionContext _connection;
    private readonly Http3Context _context;
    private int _tunnelAcceptCalled;

    public Http3ExchangeControl(Http3ConnectionContext connection, Http3Context context)
    {
        _connection = connection;
        _context = context;
    }

    /// <inheritdoc />
    public bool HasResponseStarted => _context.HasFinalResponseStarted;

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
                "An interim response cannot be sent after the final HTTP/3 response has started.");
        }

        await _connection.WriteInterimResponseAsync(_context, statusCode, headers, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public bool CanTakeOver => false;

    /// <inheritdoc />
    public Stream TakeOver()
    {
        throw new InvalidOperationException(
            "HTTP/3 exchanges are multiplexed QUIC streams over a shared connection and cannot be taken over (RFC 9114 §4.2).");
    }

    /// <inheritdoc />
    public bool CanAcceptTunnel =>
        _context.ExtendedConnectProtocol is not null
        && Volatile.Read(ref _tunnelAcceptCalled) == 0
        && !_context.CancelRequested
        && !HasResponseStarted;

    /// <inheritdoc />
    /// <remarks>
    /// The guards run in the order <see cref="HttpExtendedConnectRules"/> records. HTTP/3 keeps no
    /// separate response claim: marking the final response started and registering the tunnel before
    /// the head is written is what keeps the exchange's own send path off the request stream.
    /// </remarks>
    public async ValueTask<Stream> AcceptTunnelAsync(CancellationToken cancellationToken = default)
    {
        if (_context.ExtendedConnectProtocol is null)
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

        if (_context.RequestBody.IsReset)
        {
            throw new IOException("The HTTP/3 request stream was reset before the extended CONNECT tunnel was accepted.");
        }

        if (_connection.ConnectionClosed.IsCancellationRequested)
        {
            throw new IOException("The HTTP/3 connection closed before the extended CONNECT tunnel was accepted.");
        }

        HttpExtendedConnectRules.PrepareResponseHead(_context.Response);

        _context.MarkFinalResponseStarted();

        Http3ExtendedConnectStream tunnel = new(_connection, _context);
        _context.Tunnel = tunnel;

        await tunnel.WriteHeadAsync(Http3HeaderCodec.EncodeResponseHeaders(_context), cancellationToken).ConfigureAwait(false);
        tunnel.MarkHeadCommitted();

        return tunnel;
    }
}
