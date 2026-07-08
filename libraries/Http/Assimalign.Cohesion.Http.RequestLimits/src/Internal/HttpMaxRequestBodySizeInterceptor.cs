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
internal sealed class HttpMaxRequestBodySizeInterceptor : IHttpRequestInterceptor
{
    /// <inheritdoc />
    public void AfterRequestHead(HttpRequestInterceptorContext context)
    {
        context.Features.Set<IHttpMaxRequestBodySizeFeature>(new HttpMaxRequestBodySizeFeature(context));
    }
}
