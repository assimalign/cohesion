using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Http.Connections.Internal.Http2;

/// <summary>
/// HTTP/2 <see cref="IHttpExchangeControl"/> — the per-exchange control surface offered to
/// response interceptors through <see cref="HttpResponseInterceptorContext.Control"/>. Interim
/// (<c>1xx</c>) responses are emitted as additional HEADERS blocks on the exchange's stream ahead
/// of the final response (RFC 9113 §8.1), delegated to
/// <see cref="Http2ConnectionContext.WriteInterimResponseAsync"/> which holds the connection write
/// gate so the interim HEADERS never interleave with concurrent frames. Takeover is unsupported —
/// HTTP/2 exchanges are multiplexed streams over a shared connection and the protocol removed the
/// <c>Upgrade</c> mechanism (RFC 9113 §8.6). Abort resets the single stream via the exchange's
/// cancel path (<c>RST_STREAM(CANCEL)</c>), leaving the connection's other streams intact.
/// </summary>
internal sealed class Http2ExchangeControl : IHttpExchangeControl
{
    private readonly Http2ConnectionContext _connection;
    private readonly Http2Context _context;

    public Http2ExchangeControl(Http2ConnectionContext connection, Http2Context context)
    {
        _connection = connection;
        _context = context;
    }

    /// <inheritdoc />
    public HttpExchangeDirective Directive => _context.ExchangeDirective;

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
    public void Abort() => _context.Cancel();
}
