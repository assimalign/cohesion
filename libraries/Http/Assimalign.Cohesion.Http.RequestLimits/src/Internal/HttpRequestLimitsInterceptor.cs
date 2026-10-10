namespace Assimalign.Cohesion.Http.Internal;

/// <summary>
/// Attaches every per-request limit feature this package provides, as one write-through
/// <see cref="HttpRequestLimitsFeature"/> over the parse context's knobs, on every exchange's head hook.
/// </summary>
/// <remarks>
/// Stateless: one instance serves every connection and request on the listener; all per-request state
/// lives in the feature attached to the exchange. No body hook is needed: each read-only lifecycle
/// delegates to the context's transport-owned freeze flags.
/// </remarks>
internal sealed class HttpRequestLimitsInterceptor : HttpExchangeInterceptor
{
    /// <inheritdoc />
    public override HttpInterceptorScopes Scopes => HttpInterceptorScopes.Request;

    /// <inheritdoc />
    public override void AfterRequestHead(HttpExchangeInterceptorRequestContext context)
    {
        context.Features.Set(new HttpRequestLimitsFeature(context));
    }
}
