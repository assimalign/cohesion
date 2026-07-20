using System;

using Assimalign.Cohesion.Web.RateLimiting.Internal;

namespace Assimalign.Cohesion.Web.RateLimiting;

/// <summary>
/// Pipeline-builder members that add inbound rate limiting to a web application.
/// </summary>
/// <remarks>
/// Register <c>UseRateLimiting</c> early — after <c>UseForwardedHeaders</c> (so client-address
/// partition keys see the effective client identity) and before <c>UseRouting</c> (so it can gate
/// matched endpoints against their per-endpoint policy at the route-match seam). Composition is
/// dependency-free: the options are captured at builder time and no request-time service location
/// occurs. The limiters are built once and live for the application lifetime.
/// </remarks>
public static class RateLimitingExtensions
{
    extension(IWebApplicationPipelineBuilder builder)
    {
        /// <summary>
        /// Adds the rate-limiting middleware to the pipeline. With no configuration the middleware applies
        /// no limit — only a configured <see cref="RateLimitingOptions.GlobalPolicy"/> or endpoints
        /// carrying <see cref="RateLimitingMetadata"/> are governed.
        /// </summary>
        /// <param name="configure">An optional callback to configure the global limiter, named policies, and rejection handling.</param>
        /// <returns>The same pipeline builder for chaining.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="builder"/> is <see langword="null"/>.</exception>
        public IWebApplicationPipelineBuilder UseRateLimiting(Action<RateLimitingOptions>? configure = null)
        {
            ArgumentNullException.ThrowIfNull(builder);

            RateLimitingOptions options = new();
            configure?.Invoke(options);

            return builder.Use(new RateLimitingMiddleware(options));
        }
    }
}
