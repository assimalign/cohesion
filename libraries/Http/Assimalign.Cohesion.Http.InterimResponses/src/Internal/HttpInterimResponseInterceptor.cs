namespace Assimalign.Cohesion.Http.Internal;

/// <summary>
/// The response interceptor that makes interim (<c>1xx</c>) responses available on an exchange. It
/// wraps the transport's exchange control
/// (<see cref="HttpResponseInterceptorContext.Control"/>) in a typed
/// <see cref="IHttpInterimResponseFeature"/> and installs it on the exchange's feature collection —
/// the seam by which interim responses plug into the transport without the transport depending on
/// this package.
/// </summary>
internal sealed class HttpInterimResponseInterceptor : HttpExchangeInterceptor
{
    /// <inheritdoc />
    public override HttpInterceptorScopes Scopes => HttpInterceptorScopes.Response;

    public override void BeforeResponse(HttpResponseInterceptorContext context)
    {
        // A transport that does not offer exchange control (a foreign IHttpContext, a hand-built
        // test context) leaves Control null — install nothing so
        // context.Features.Get<IHttpInterimResponseFeature>() resolves to null and callers see the
        // capability as absent rather than as a broken feature.
        if (context.Control is { } control)
        {
            context.Features.Set(new HttpInterimResponseFeature(control));
        }
    }
}
