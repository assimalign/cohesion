using Assimalign.Cohesion.Http.Internal;

namespace Assimalign.Cohesion.Http;

/// <summary>
/// The response interceptor that makes incremental response streaming available on an exchange. It
/// wraps the transport's raw response body sink in a typed <see cref="IHttpResponseStreamingFeature"/>
/// and installs it on the exchange's feature collection — the seam by which streaming plugs into the
/// transport without the transport depending on this package.
/// </summary>
public sealed class HttpResponseStreamingInterceptor : HttpExchangeInterceptor
{
    /// <summary>
    /// Creates the response interceptor that makes <see cref="IHttpResponseStreamingFeature"/>
    /// available on every exchange served by the transport it is registered on.
    /// </summary>
    public HttpResponseStreamingInterceptor()
    {
    }

    /// <inheritdoc />
    public override HttpInterceptorScopes Scopes => HttpInterceptorScopes.Response;

    /// <inheritdoc />
    public override void BeforeResponse(HttpExchangeInterceptorResponseContext context)
    {
        context.Features.Set(new HttpResponseStreamingFeature(context.ResponseBody, context.Control));
    }
}
