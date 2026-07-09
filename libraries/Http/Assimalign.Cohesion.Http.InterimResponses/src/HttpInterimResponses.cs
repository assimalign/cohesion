using Assimalign.Cohesion.Http.Internal;

namespace Assimalign.Cohesion.Http;

/// <summary>
/// Entry point for enabling interim (<c>1xx</c>) responses — <c>100 Continue</c> (RFC 9110 §10.1.1)
/// and <c>103 Early Hints</c> (RFC 8297) — on an HTTP server transport.
/// </summary>
/// <remarks>
/// <para>
/// Register the interceptor this factory produces on the transport's response-interceptor list
/// (<c>HttpConnectionListenerOptions.Interceptors</c>), the same opt-in way
/// <c>Http.Streaming</c> and <c>Http.ProtocolUpgrade</c> plug in:
/// </para>
/// <code>
/// options.Interceptors.Add(HttpInterimResponses.CreateInterceptor());
/// </code>
/// <para>
/// Doing so installs an <see cref="IHttpInterimResponseFeature"/> on every exchange, which a handler
/// resolves via <c>context.InterimResponse</c> (or <c>context.SendEarlyHintsAsync(...)</c>). The
/// interceptor wraps the transport's exchange control
/// (<see cref="HttpExchangeInterceptorResponseContext.Control"/>); the transport never references this
/// package — it only invokes the <see cref="IHttpResponseInterceptor"/> and exposes the control.
/// </para>
/// <para>
/// The HTTP/1.1 transport additionally emits <c>100 Continue</c> automatically for a request that
/// carries <c>Expect: 100-continue</c> with a framed body (a wire-level interop concern), independent
/// of this package — so the large-upload handshake works even when this interceptor is not
/// registered.
/// </para>
/// </remarks>
public static class HttpInterimResponses
{
    /// <summary>
    /// Creates the response interceptor that makes <see cref="IHttpInterimResponseFeature"/>
    /// available on every exchange served by the transport it is registered on.
    /// </summary>
    /// <returns>The response interceptor to add to the transport's response-interceptor list.</returns>
    public static IHttpExchangeInterceptor CreateInterceptor() => new HttpInterimResponseInterceptor();
}
