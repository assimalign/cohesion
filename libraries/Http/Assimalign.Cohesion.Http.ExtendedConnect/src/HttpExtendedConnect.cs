using Assimalign.Cohesion.Http.Internal;

namespace Assimalign.Cohesion.Http;

/// <summary>
/// Entry point for enabling extended CONNECT (RFC 8441 for HTTP/2, RFC 9220 for HTTP/3) on an HTTP
/// server transport: the stream tunnel a WebSocket over HTTP/2 or HTTP/3 bootstraps on.
/// </summary>
/// <remarks>
/// <para>
/// The capability is wired entirely through the transport's interceptor seam — the transport
/// never references this package. Register the single interceptor this factory produces on the
/// transport's listener options:
/// </para>
/// <code>
/// options.Interceptors.Add(HttpExtendedConnect.CreateInterceptor());
/// </code>
/// <para>
/// Its <see cref="IHttpExchangeInterceptor.AfterRequestHead"/> hook reads the validated
/// <c>:protocol</c> the transport passes on
/// (<see cref="HttpExchangeInterceptorRequestContext.Protocol"/>) and installs the
/// <see cref="IHttpExtendedConnectFeature"/> that <c>context.ExtendedConnect</c> reads. Its
/// <see cref="IHttpExchangeInterceptor.BeforeResponse"/> hook binds that feature to the transport's
/// exchange control (<see cref="HttpExchangeInterceptorResponseContext.Control"/>), whose
/// <see cref="IHttpExchangeControl.AcceptTunnelAsync"/> commits the <c>200</c> and returns the
/// tunnel. The instance is stateless and shared, so the feature itself carries the exchange's
/// state between the two hooks.
/// </para>
/// <para>
/// It declares <see cref="HttpInterceptorScopes.Request"/> and joins the response phase only of an
/// extended CONNECT (<see cref="HttpExchangeInterceptorRequestContext.AddResponseInterceptor"/>), so
/// registering it costs any other exchange one version check and nothing more: the transport builds
/// no response sink or exchange control for it on its account.
/// </para>
/// <para>
/// The HTTP/2 and HTTP/3 transports advertise <c>SETTINGS_ENABLE_CONNECT_PROTOCOL</c> whether or not
/// the interceptor is registered. Without it a client's extended CONNECT reaches the application as
/// an ordinary <c>CONNECT</c> with no <c>context.ExtendedConnect</c>, and a WebSocket over HTTP/2
/// or HTTP/3 cannot be accepted.
/// </para>
/// </remarks>
public static class HttpExtendedConnect
{
    /// <summary>
    /// Creates the exchange interceptor that surfaces an HTTP/2 or HTTP/3 extended CONNECT as an
    /// <see cref="IHttpExtendedConnectFeature"/> over the transport's exchange control. Add it to
    /// the transport's interceptor list.
    /// </summary>
    /// <returns>The interceptor to add to the transport's interceptor list.</returns>
    public static IHttpExchangeInterceptor CreateInterceptor() => new HttpExtendedConnectInterceptor();
}
