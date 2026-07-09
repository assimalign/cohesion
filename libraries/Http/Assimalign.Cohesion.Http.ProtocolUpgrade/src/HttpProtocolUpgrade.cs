namespace Assimalign.Cohesion.Http;

/// <summary>
/// Entry point for enabling HTTP/1.1 protocol upgrades (RFC 9110 §7.8,
/// <c>101 Switching Protocols</c>) and <c>CONNECT</c> tunnelling (RFC 9110 §9.3.6) on an HTTP
/// server transport.
/// </summary>
/// <remarks>
/// <para>
/// The capability is wired entirely through the transport's interceptor seam — the transport
/// never references this package. Register the single interceptor this factory produces on the
/// transport's listener options:
/// </para>
/// <code>
/// options.Interceptors.Add(HttpProtocolUpgrade.CreateInterceptor());
/// </code>
/// <para>
/// One interceptor participates in both phases of the exchange: its
/// <see cref="IHttpExchangeInterceptor.AfterRequestHead"/> hook detects the wire-level transition
/// signal on the parsed request head, and its
/// <see cref="IHttpExchangeInterceptor.BeforeResponse"/> hook converts that detection into an
/// <see cref="IHttpProtocolUpgradeFeature"/> wrapping the transport's exchange control
/// (<see cref="HttpExchangeInterceptorResponseContext.Control"/>, whose takeover surrenders the
/// connection). A handler then reads <c>context.Upgrade</c> and accepts the transition to receive
/// the raw duplex stream. The instance is stateless and shared, so the detection state crosses
/// between its hooks through the exchange's feature collection.
/// </para>
/// </remarks>
public static class HttpProtocolUpgrade
{
    /// <summary>
    /// Creates the exchange interceptor that detects HTTP/1.1 upgrade / <c>CONNECT</c> transition
    /// signals and surfaces them as an <see cref="IHttpProtocolUpgradeFeature"/> over the
    /// transport's exchange control (takeover). Add it to the transport's interceptor list.
    /// </summary>
    /// <returns>The interceptor to add to the transport's interceptor list.</returns>
    public static IHttpExchangeInterceptor CreateInterceptor() => new HttpProtocolUpgradeInterceptor();
}
