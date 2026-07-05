using System;

namespace Assimalign.Cohesion.Http;

/// <summary>
/// Surfaces the per-request limit features for the current exchange.
/// </summary>
public static class HttpRequestLimitsExtensions
{
    extension(IHttpContext context)
    {
        /// <summary>
        /// Gets the max-request-body-size feature for this exchange, or <see langword="null"/>
        /// when the max-request-body-size interceptor is not registered on the server.
        /// </summary>
        /// <exception cref="ArgumentNullException"><paramref name="context"/> is <see langword="null"/>.</exception>
        public IHttpMaxRequestBodySizeFeature? MaxRequestBodySize
        {
            get
            {
                ArgumentNullException.ThrowIfNull(context);
                return context.Features.Get<IHttpMaxRequestBodySizeFeature>();
            }
        }
    }
}
