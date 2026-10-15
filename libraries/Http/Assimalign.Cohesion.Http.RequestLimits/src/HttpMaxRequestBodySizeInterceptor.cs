using Assimalign.Cohesion.Http.Internal;

namespace Assimalign.Cohesion.Http;

/// <summary>
/// Attaches a per-request <see cref="IHttpMaxRequestBodySizeFeature"/> — a write-through view
/// over the parse context's body-size knob — on every exchange's head hook.
/// </summary>
/// <remarks>
/// Stateless: one instance serves every connection and request on the listener; all per-request
/// state lives in the feature attached to the exchange. No body hook is needed — the feature's
/// read-only lifecycle delegates to the context's transport-owned freeze flag rather than being
/// copied at a fixed point in the parse.
/// </remarks>
public sealed class HttpMaxRequestBodySizeInterceptor : HttpExchangeInterceptor
{
    /// <summary>
    /// Creates the interceptor that surfaces the transport's effective per-request body-size cap
    /// as a typed <see cref="IHttpMaxRequestBodySizeFeature"/> on every exchange, allowing the
    /// cap to be observed — and, while the request body has not started to be read, adjusted —
    /// per request.
    /// </summary>
    public HttpMaxRequestBodySizeInterceptor()
    {
    }

    /// <inheritdoc />
    public override HttpInterceptorScopes Scopes => HttpInterceptorScopes.Request;

    /// <inheritdoc />
    public override void AfterRequestHead(HttpExchangeInterceptorRequestContext context)
    {
        context.Features.Set<IHttpMaxRequestBodySizeFeature>(new HttpMaxRequestBodySizeFeature(context));
    }
}
