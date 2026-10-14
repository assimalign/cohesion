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
    private HttpResponseStreamingInterceptor()
    {
    }

    /// <summary>
    /// Creates the response interceptor that makes <see cref="IHttpResponseStreamingFeature"/>
    /// available on every exchange served by the transport it is registered on.
    /// </summary>
    /// <returns>The response interceptor to add to the transport's response-interceptor list.</returns>
    public static HttpResponseStreamingInterceptor Create() => new HttpResponseStreamingInterceptor();

    /// <inheritdoc />
    public override HttpInterceptorScopes Scopes => HttpInterceptorScopes.Response;

    /// <inheritdoc />
    public override void BeforeResponse(HttpExchangeInterceptorResponseContext context)
    {
        context.Features.Set(new HttpResponseStreamingFeature(context.ResponseBody, context.Control));
    }
}
