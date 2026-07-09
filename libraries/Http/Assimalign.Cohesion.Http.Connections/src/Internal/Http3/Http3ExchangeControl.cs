using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Http.Connections.Internal.Http3;

/// <summary>
/// HTTP/3 <see cref="IHttpExchangeControl"/> — the per-exchange control surface offered to
/// response interceptors through <see cref="HttpResponseInterceptorContext.Control"/>. Interim
/// (<c>1xx</c>) responses are emitted as additional QPACK-encoded HEADERS frames on the request
/// stream ahead of the final HEADERS frame (RFC 9114 §4.1), delegated to
/// <see cref="Http3ConnectionContext.WriteInterimResponseAsync"/>. Takeover is unsupported —
/// HTTP/3 exchanges are multiplexed QUIC streams over a shared connection (RFC 9114 §4.2).
/// Aborting is not a control mechanism — it is the application-owned
/// <see cref="IHttpContext.Cancel"/>, which the send path honors by resetting the single request
/// stream, leaving the QUIC connection's other streams intact.
/// </summary>
internal sealed class Http3ExchangeControl : IHttpExchangeControl
{
    private readonly Http3ConnectionContext _connection;
    private readonly Http3Context _context;

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

}
