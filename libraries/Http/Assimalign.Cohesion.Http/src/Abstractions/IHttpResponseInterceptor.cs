namespace Assimalign.Cohesion.Http;

/// <summary>
/// A server-side interception point applied while the response pipeline for an exchange is being
/// set up, before the application handler runs. Response interceptors are the symmetric
/// counterpart to <see cref="IHttpRequestInterceptor"/>: they let feature packages participate in
/// the response — attaching typed <see cref="IHttpFeature"/>s and tapping the transport's raw
/// response body sink — <b>without the transport taking a dependency on any feature package</b>.
/// </summary>
/// <remarks>
/// <para>
/// This is the seam that keeps response-side capabilities (incremental streaming, Server-Sent
/// Events, and later response compression) out of both the protocol core and the transport. A
/// feature package ships an <see cref="IHttpResponseInterceptor"/> that, in
/// <see cref="OnResponse"/>, wraps the transport-provided
/// <see cref="HttpResponseInterceptorContext.ResponseBody"/> sink and installs a typed feature on
/// <see cref="HttpResponseInterceptorContext.Features"/>; the application resolves that feature
/// and drives it. The transport invokes the interceptors and enforces the wire framing, but never
/// references the feature package — exactly how <c>Http.RequestLimits</c> plugs into request
/// parsing via <see cref="IHttpRequestInterceptor"/>.
/// </para>
/// <para>
/// Interceptors are registered on the server transport's listener options and are invoked in
/// registration order. A registered instance is shared across <b>all</b> connections and requests
/// served by the listener: implementations must be stateless and thread-safe, and any per-request
/// state belongs in <see cref="HttpResponseInterceptorContext.Features"/>, never in instance
/// fields.
/// </para>
/// <para>
/// The hook ships a default implementation so an interceptor overrides only what it needs; future
/// interception points are added the same way without breaking existing implementations. Hooks run
/// inline while the exchange is being set up, before any response byte is produced, so they must be
/// CPU-only — no I/O, no blocking waits.
/// </para>
/// </remarks>
public interface IHttpResponseInterceptor
{
    /// <summary>
    /// Called once per exchange, after the request head has been parsed and before the application
    /// handler runs. Implementations attach response features and may wrap the raw response body
    /// sink exposed on the context; nothing has been written to the wire yet, so the response
    /// status and headers are still fully mutable by the application afterward.
    /// </summary>
    /// <param name="context">The response-setup view of the exchange.</param>
    void OnResponse(HttpResponseInterceptorContext context)
    {
    }
}
