namespace Assimalign.Cohesion.Http.Internal;

/// <summary>
/// The response interceptor that makes incremental response streaming available on an exchange. It
/// wraps the transport's raw response body sink in a typed <see cref="IHttpResponseStreamingFeature"/>
/// and installs it on the exchange's feature collection — the seam by which streaming plugs into the
/// transport without the transport depending on this package.
/// </summary>
internal sealed class HttpResponseStreamingInterceptor : HttpExchangeInterceptor
{
    /// <inheritdoc />
    public override HttpInterceptorScopes Scopes => HttpInterceptorScopes.Response;

    public override void BeforeResponse(HttpResponseInterceptorContext context)
    {
        context.Features.Set(new HttpResponseStreamingFeature(context.ResponseBody));
    }
}
