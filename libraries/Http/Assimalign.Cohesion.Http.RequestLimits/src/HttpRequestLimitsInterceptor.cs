using Assimalign.Cohesion.Http.Internal;

namespace Assimalign.Cohesion.Http;

/// <summary>
/// Attaches every per-request limit feature this package provides, as one write-through
/// <see cref="HttpRequestLimitsFeature"/> over the parse context's knobs, on every exchange's head hook.
/// </summary>
/// <remarks>
/// Stateless: one instance serves every connection and request on the listener; all per-request state
/// lives in the feature attached to the exchange. No body hook is needed: each read-only lifecycle
/// delegates to the context's transport-owned freeze flags.
/// </remarks>
public sealed class HttpRequestLimitsInterceptor : HttpExchangeInterceptor
{
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
    /// <see cref="HttpMaxRequestBodySizeInterceptor()"/> attaches: register one or the other.
    /// </remarks>
    public HttpRequestLimitsInterceptor()
    {
    }

    /// <inheritdoc />
    public override HttpInterceptorScopes Scopes => HttpInterceptorScopes.Request;

    /// <inheritdoc />
    public override void AfterRequestHead(HttpExchangeInterceptorRequestContext context)
    {
        context.Features.Set(new HttpRequestLimitsFeature(context));
    }
}
