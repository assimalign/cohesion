using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Http.Connections.Internal.Http1;

internal sealed class Http1Context : TransportHttpContext
{
    private readonly Http1RequestBodyStream _requestBody;

    public Http1Context(
        Http1Request request,
        Http1Response response,
        HttpConnectionInfo connectionInfo,
        CancellationToken requestAborted,
        bool keepAlive,
        Http1RequestBodyStream requestBody,
        IHttpFeatureCollection? features = null)
        : base(HttpVersion.Http11, request, response, connectionInfo, requestAborted, features)
    {
        KeepAlive = keepAlive;
        _requestBody = requestBody;
        // Back-reference for the lazy Expect: 100-continue solicitation — the body stream checks
        // whether the final response has started before emitting the interim response.
        requestBody.SetOwner(this);
    }

    public bool KeepAlive { get; set; }

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
    /// HTTP/1.1 is the one version whose exchange can be handed off (it owns its whole
    /// connection), so a finalized-out-of-band exchange reports
    /// <see cref="HttpExchangeDirective.TakeOver"/>; otherwise the base's abort/continue
    /// derivation applies.
    /// </summary>
    internal override HttpExchangeDirective ExchangeDirective =>
        ResponseFinalized ? HttpExchangeDirective.TakeOver : base.ExchangeDirective;

    /// <summary>
    /// Consumes and discards any request body the application did not read, so the connection
    /// realigns on the next request's framing before a keep-alive reuse. Enforces the same body-size
    /// cap and minimum data rate as a normal read; a violation, a malformed body, or a wire failure
    /// returns <see langword="false"/> so the caller closes the connection instead of reusing it.
    /// </summary>
    /// <param name="cancellationToken">The ambient connection token.</param>
    /// <returns><see langword="true"/> when the body drained and the connection realigned; otherwise <see langword="false"/>.</returns>
    public ValueTask<bool> DrainRequestBodyAsync(CancellationToken cancellationToken)
        => _requestBody.DrainAsync(cancellationToken);
}
