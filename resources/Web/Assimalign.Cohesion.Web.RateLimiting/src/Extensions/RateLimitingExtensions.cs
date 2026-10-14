using System;

using Assimalign.Cohesion.Web.RateLimiting.Internal;

namespace Assimalign.Cohesion.Web.RateLimiting;

/// <summary>
/// Pipeline-builder members that add inbound rate limiting to a web application.
/// </summary>
/// <remarks>
/// Register <c>UseRateLimiting</c> after <c>UseForwardedHeaders</c> (so client-address partition keys
/// see the effective client identity) and after <c>UseRouting</c> (so the request's endpoint and its
/// <see cref="RateLimitingMetadata"/> are published when the middleware runs). Composition is
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
        /// <remarks>
        /// <para>
        /// Register it after <c>UseRouting</c>. The middleware reads the endpoint <c>UseRouting</c>
        /// published and acquires the endpoint's policy asynchronously, so a queueing limiter holds the
        /// request until a permit is released or the request is cancelled. A rejected request is answered
        /// without running the endpoint.
        /// </para>
        /// <para>
        /// Registered ahead of <c>UseRouting</c>, the global limiter still applies to every request, but
        /// no endpoint is known yet. An endpoint whose <see cref="RateLimitingMetadata"/> names a policy
        /// then fails with an <see cref="InvalidOperationException"/> when it is dispatched, instead of
        /// running without its limit.
        /// </para>
        /// </remarks>
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
