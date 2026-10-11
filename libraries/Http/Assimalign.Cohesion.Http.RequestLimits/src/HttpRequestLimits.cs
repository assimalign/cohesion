using Assimalign.Cohesion.Http.Internal;

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
/// Register the interceptor <em>first</em> so the typed features it attaches
/// (<see cref="IHttpMaxRequestBodySizeFeature"/>, <see cref="IHttpMinRequestBodyDataRateFeature"/>,
/// <see cref="IHttpMinResponseDataRateFeature"/>) are visible to every later interceptor's head hook.
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
    public static IHttpExchangeInterceptor CreateMaxRequestBodySizeInterceptor()
    {
        return new HttpMaxRequestBodySizeInterceptor();
    }

    /// <summary>
    /// Creates the interceptor that surfaces every per-request limit this package provides on every
    /// exchange: the body-size cap as a typed <see cref="IHttpMaxRequestBodySizeFeature"/>, the minimum
    /// request-body data rate as a typed <see cref="IHttpMinRequestBodyDataRateFeature"/> (#1383), and the
    /// minimum response data rate as a typed <see cref="IHttpMinResponseDataRateFeature"/> (#1384). Each can
    /// be observed any time; the request-body knobs can be adjusted until the transport starts reading the
    /// request body, and the response rate until it starts writing the response. An endpoint that serves a
    /// body which legitimately idles (a client-streaming call) disables the request-body rate for its own
    /// requests, and every other request keeps the listener's.
    /// </summary>
    /// <remarks>
    /// One feature object carries every contract, so an exchange pays one allocation and one
    /// feature-collection slot for all of them, as it did for the body-size feature alone. It registers
    /// under the body-size feature's name, so it replaces the feature
    /// <see cref="CreateMaxRequestBodySizeInterceptor"/> attaches: register one or the other.
    /// </remarks>
    /// <returns>A stateless interceptor to register on the server's listener options.</returns>
    public static IHttpExchangeInterceptor CreateInterceptor()
    {
        return new HttpRequestLimitsInterceptor();
    }
}
