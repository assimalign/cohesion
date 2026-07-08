namespace Assimalign.Cohesion.Http;

/// <summary>
/// Entry point for enabling HTTP/1.1 protocol upgrades (RFC 9110 §7.8,
/// <c>101 Switching Protocols</c>) and <c>CONNECT</c> tunnelling (RFC 9110 §9.3.6) on an HTTP
/// server transport.
/// </summary>
/// <remarks>
/// <para>
/// The capability is wired entirely through the transport's interceptor seams — the transport
/// never references this package. Register <b>both</b> interceptors this factory produces on the
/// transport's listener options:
/// </para>
/// <code>
/// options.Interceptors.Add(HttpProtocolUpgrade.CreateRequestInterceptor());
/// options.ResponseInterceptors.Add(HttpProtocolUpgrade.CreateResponseInterceptor());
/// </code>
/// <para>
/// The request interceptor detects the wire-level transition signal on the parsed request head;
/// the response interceptor converts that detection into an <see cref="IHttpProtocolUpgradeFeature"/>
/// wrapping the transport's connection-takeover capability
/// (<see cref="HttpResponseInterceptorContext.ConnectionTakeover"/>). A handler then reads
/// <c>context.Upgrade</c> and accepts the transition to receive the raw duplex stream. Both hooks
/// are required because detection needs the request head while the takeover capability is offered
/// on the response seam; each is stateless, so the two instances coordinate through the exchange's
/// feature collection.
/// </para>
/// </remarks>
public static class HttpProtocolUpgrade
{
    /// <summary>
    /// Creates the request interceptor that detects the HTTP/1.1 upgrade / <c>CONNECT</c>
    /// transition signal on each request head. Add it to the transport's request-interceptor
    /// list, paired with <see cref="CreateResponseInterceptor"/>.
    /// </summary>
    /// <returns>The request interceptor to add to the transport's request-interceptor list.</returns>
    public static IHttpRequestInterceptor CreateRequestInterceptor() => new HttpProtocolUpgradeInterceptor();

    /// <summary>
    /// Creates the response interceptor that surfaces a detected transition as an
    /// <see cref="IHttpProtocolUpgradeFeature"/> over the transport's connection-takeover
    /// capability. Add it to the transport's response-interceptor list, paired with
    /// <see cref="CreateRequestInterceptor"/>.
    /// </summary>
    /// <returns>The response interceptor to add to the transport's response-interceptor list.</returns>
    public static IHttpResponseInterceptor CreateResponseInterceptor() => new HttpProtocolUpgradeInterceptor();
}
