namespace Assimalign.Cohesion.Http;

/// <summary>
/// Entry point for the per-request limit features this package provides.
/// </summary>
/// <remarks>
/// <para>
/// The interceptors created here are registered on the server transport's listener options by
/// the composition root (for example the web host builder). They are stateless and thread-safe;
/// one instance serves every connection and request on the listener.
/// </para>
/// <para>
/// Register the max-request-body-size interceptor <em>first</em> so the typed
/// <see cref="IHttpMaxRequestBodySizeFeature"/> it attaches is visible to every later
/// interceptor's head hook.
/// </para>
/// </remarks>
public static class HttpRequestLimits
{
    /// <summary>
    /// Creates the interceptor that surfaces the transport's effective per-request body-size cap
    /// as a typed <see cref="IHttpMaxRequestBodySizeFeature"/> on every exchange, allowing the
    /// cap to be observed — and, while the request body has not started to be read, adjusted —
    /// per request.
    /// </summary>
    /// <returns>A stateless interceptor to register on the server's listener options.</returns>
    public static IHttpRequestInterceptor CreateMaxRequestBodySizeInterceptor()
    {
        return new HttpMaxRequestBodySizeInterceptor();
    }
}
