using System;

namespace Assimalign.Cohesion.Http;

/// <summary>
/// Ergonomic access to the <see cref="IHttpResponseStreamingFeature"/> installed on an exchange by
/// the response-streaming interceptor, so handlers can reach the incremental write/flush seam
/// without spelling out the feature-collection lookup at every call site.
/// </summary>
public static class HttpResponseStreamingExtensions
{
    extension(IHttpResponse response)
    {
        /// <summary>
        /// Gets a value indicating whether incremental response streaming is available for this
        /// exchange (the response-streaming interceptor is registered and installed its feature).
        /// </summary>
        public bool SupportsStreaming
        {
            get
            {
                ArgumentNullException.ThrowIfNull(response);
                return response.HttpContext.Features.Get<IHttpResponseStreamingFeature>() is not null;
            }
        }

        /// <summary>
        /// Gets the <see cref="IHttpResponseStreamingFeature"/> for this exchange.
        /// </summary>
        /// <exception cref="NotSupportedException">
        /// Response streaming is not enabled for this exchange (its interceptor was not registered on
        /// the transport). Register <see cref="HttpResponseStreaming.CreateInterceptor"/> on the
        /// transport's response interceptors, or check <see cref="SupportsStreaming"/> first.
        /// </exception>
        public IHttpResponseStreamingFeature Streaming
        {
            get
            {
                ArgumentNullException.ThrowIfNull(response);
                return response.HttpContext.Features.Get<IHttpResponseStreamingFeature>()
                    ?? throw new NotSupportedException(
                        "Response streaming is not enabled for this exchange. Register HttpResponseStreaming.CreateInterceptor() " +
                        "on the transport's ResponseInterceptors.");
            }
        }
    }
}
