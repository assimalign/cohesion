using Assimalign.Cohesion.Http.Internal;

namespace Assimalign.Cohesion.Http;

/// <summary>
/// Entry point for enabling incremental response streaming on an HTTP server transport.
/// </summary>
/// <remarks>
/// Register the interceptor this factory produces on the transport's response-interceptor list
/// (<c>HttpConnectionListenerOptions.ResponseInterceptors</c>), the same way
/// <c>Http.RequestLimits</c> registers a request interceptor. Doing so installs an
/// <see cref="IHttpResponseStreamingFeature"/> on every exchange, which a handler resolves via
/// <c>context.Response.Streaming</c>. The transport never references this package — it only invokes
/// the <see cref="IHttpResponseInterceptor"/> and exposes its raw response body sink.
/// </remarks>
public static class HttpResponseStreaming
{
    /// <summary>
    /// Creates the response interceptor that makes <see cref="IHttpResponseStreamingFeature"/>
    /// available on every exchange served by the transport it is registered on.
    /// </summary>
    /// <returns>The response interceptor to add to the transport's response-interceptor list.</returns>
    public static IHttpResponseInterceptor CreateInterceptor() => new HttpResponseStreamingInterceptor();
}
