using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Http.Connections.Internal;

internal sealed class Http1Context : TransportHttpContext
{
    private readonly Http1RequestBodyStream _requestBody;

    // Volatile: a graceful close clears it from the thread that began the close while the exchange's
    // own thread reads it to frame the response head (Http1ConnectionContext.BeginGracefulClose).
    private volatile bool _keepAlive;

    public Http1Context(
        in TransportHttpRequestHead requestHead,
        HttpConnectionInfo connectionInfo,
        CancellationToken requestAborted,
        bool keepAlive,
        Http1RequestBodyStream requestBody,
        IHttpFeatureCollection? features = null)
        : base(HttpVersion.Http11, requestHead, connectionInfo, requestAborted, features)
    {
        KeepAlive = keepAlive;
        _requestBody = requestBody;
        // Back-reference for the lazy Expect: 100-continue solicitation — the body stream checks
        // whether the final response has started before emitting the interim response.
        requestBody.SetOwner(this);
    }

    /// <summary>
    /// Whether the connection carries another request after this exchange. When it is
    /// <see langword="false"/> as the response head is committed, the head carries
    /// <c>Connection: close</c> (RFC 9112 §9.6).
    /// </summary>
    public bool KeepAlive
    {
        get => _keepAlive;
        set => _keepAlive = value;
    }

    /// <summary>
    /// Whether the response for this exchange was finalized out-of-band — the connection was
    /// taken over via <see cref="Http1ExchangeControl.TakeOver"/> (HTTP/1.1 upgrade / CONNECT
    /// accept path) and the transition response was written directly to the surrendered raw
    /// stream. When set, <see cref="Http1ConnectionContext.SendAsync"/> is a no-op so the
    /// transport never writes HTTP framing onto what is now a raw byte stream
    /// (RFC 9110 §7.8 / §9.3.6).
    /// </summary>
    public bool ResponseFinalized { get; set; }

    /// <summary>
    /// HTTP/1.1 is the one version whose exchange can hand off its whole connection, so a
    /// finalized-out-of-band exchange reports <see cref="HttpExchangeDirective.TakeOver"/>;
    /// otherwise the base's abort/continue derivation applies.
    /// </summary>
    internal override HttpExchangeDirective ExchangeDirective =>
        ResponseFinalized ? HttpExchangeDirective.TakeOver : base.ExchangeDirective;

    /// <summary>
    /// The status the transport answers this exchange with because reading its request body after
    /// dispatch failed on the client's side, or <see langword="null"/> when it did not: <c>400</c> for a
    /// malformed chunked framing or trailer section (RFC 9112 §5.1, #1333), and the latched limit
    /// status for a body over the size cap (<c>413</c>) or below the minimum data rate (<c>408</c>,
    /// #1339). The rejection replaces a response that has not started, and the connection closes.
    /// </summary>
    public HttpStatusCode? RequestBodyRejectedStatusCode =>
        _requestBody.IsMalformed ? HttpStatusCode.BadRequest : _requestBody.RejectedStatusCode;

    /// <summary>
    /// Consumes and discards any request body the application did not read, so the connection
    /// realigns on the next request's framing before a keep-alive reuse. Enforces the same body-size
    /// cap and minimum data rate as a normal read; a violation, a malformed body, an earlier read that
    /// stopped inside the chunked framing, or a wire failure returns <see langword="false"/> so the
    /// caller closes the connection instead of reusing it.
    /// </summary>
    /// <param name="cancellationToken">The ambient connection token.</param>
    /// <returns><see langword="true"/> when the body drained and the connection realigned; otherwise <see langword="false"/>.</returns>
    public ValueTask<bool> DrainRequestBodyAsync(CancellationToken cancellationToken)
        => _requestBody.DrainAsync(cancellationToken);
}
