namespace Assimalign.Cohesion.Http.Connections.Tests.TestObjects;

/// <summary>
/// A response-scoped interceptor that records the TLS connection feature its
/// <see cref="BeforeResponse"/> hook observes on the exchange's features.
/// </summary>
internal sealed class TlsObservingInterceptor : HttpExchangeInterceptor
{
    public override HttpInterceptorScopes Scopes => HttpInterceptorScopes.Response;

    public IHttpTlsConnectionFeature? Observed { get; private set; }

    public override void BeforeResponse(HttpExchangeInterceptorResponseContext context)
    {
        Observed = context.Features.Get<IHttpTlsConnectionFeature>();
    }
}
