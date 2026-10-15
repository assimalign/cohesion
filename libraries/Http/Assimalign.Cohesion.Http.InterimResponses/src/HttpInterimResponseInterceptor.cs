using Assimalign.Cohesion.Http.Internal;

namespace Assimalign.Cohesion.Http;

/// <summary>
/// The response interceptor that makes interim (<c>1xx</c>) responses available on an exchange. It
/// wraps the transport's exchange control
/// (<see cref="HttpExchangeInterceptorResponseContext.Control"/>) in a typed
/// <see cref="IHttpInterimResponseFeature"/> and installs it on the exchange's feature collection —
/// the seam by which interim responses plug into the transport without the transport depending on
/// this package.
/// </summary>
public sealed class HttpInterimResponseInterceptor : HttpExchangeInterceptor
{
    /// <summary>
    /// Creates the response interceptor that makes <see cref="IHttpInterimResponseFeature"/>
    /// available on every exchange served by the transport it is registered on.
    /// </summary>
    public HttpInterimResponseInterceptor()
    {
    }

    /// <inheritdoc />
    public override HttpInterceptorScopes Scopes => HttpInterceptorScopes.Response;

    /// <inheritdoc />
    public override void BeforeResponse(HttpExchangeInterceptorResponseContext context)
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
