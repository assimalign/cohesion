using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Http.Connections.Internal.Http1;

internal sealed class Http1Context : TransportHttpContext
{
    public Http1Context(
        Http1Request request,
        Http1Response response,
        HttpConnectionInfo connectionInfo,
        CancellationToken requestAborted,
        bool keepAlive,
        IHttpFeatureCollection? features = null)
        : base(HttpVersion.Http11, request, response, connectionInfo, requestAborted, features)
    {
        KeepAlive = keepAlive;
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
}
