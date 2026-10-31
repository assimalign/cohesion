using System;
using System.Linq;

using Assimalign.Cohesion.Web.RateLimiting.Internal;

namespace Assimalign.Cohesion.Web.RateLimiting;

/// <summary>
/// Pipeline-builder extension that places inbound rate limiting in the Web application middleware
/// pipeline.
/// </summary>
/// <remarks>
/// Place <c>UseRateLimiting</c> after <c>UseForwardedHeaders</c> (so client-address partition keys
/// see the effective client identity) and after <c>UseRouting</c> (so the request's endpoint and its
/// <see cref="RateLimitingMetadata"/> are published when the middleware runs). The policies are
/// configured where the middleware is registered, with
/// <c>builder.Services.AddRateLimiting(rateLimiting =&gt; ...)</c> (<see cref="RateLimitingBuilder"/>), and
/// no request-time service location occurs. The limiters are built once and live for the application
/// lifetime.
/// </remarks>
public static class RateLimitingExtensions
{
    extension(IWebApplicationPipelineBuilder builder)
    {
        /// <summary>
        /// Places the rate-limiting middleware the application registered with
        /// <c>builder.Services.AddRateLimiting(rateLimiting =&gt; ...)</c>, or one with the defaults when it
        /// registered none. The defaults apply no global limit and name no policy, so only endpoints whose
        /// <see cref="RateLimitingMetadata"/> carries an inline <see cref="RateLimitingPolicy"/> are governed.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The global limiter, named policies, rejection status, and hooks are configured where the
        /// middleware is registered (<see cref="RateLimitingBuilder"/>); this verb only positions it. When
        /// the application registered rate limiting more than once, the last registration wins. Without a
        /// registration, a request for an endpoint whose metadata names a policy fails with an
        /// <see cref="InvalidOperationException"/>, because no named policy exists to resolve.
        /// </para>
        /// <para>
        /// Place it after <c>UseRouting</c>. The middleware reads the endpoint <c>UseRouting</c>
        /// published and acquires the endpoint's policy asynchronously, so a queueing limiter holds the
        /// request until a permit is released or the request is cancelled. A rejected request is answered
        /// without running the endpoint.
        /// </para>
        /// <para>
        /// Placed ahead of <c>UseRouting</c>, the global limiter still applies to every request, but no
        /// endpoint is known yet. An endpoint whose <see cref="RateLimitingMetadata"/> names a policy then
        /// fails with an <see cref="InvalidOperationException"/> when it is dispatched, instead of running
        /// without its limit.
        /// </para>
        /// </remarks>
        /// <returns>The same pipeline builder for chaining.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="builder"/> is <see langword="null"/>.</exception>
        public IWebApplicationPipelineBuilder UseRateLimiting()
        {
            ArgumentNullException.ThrowIfNull(builder);

            return builder.Use(static (context, next) =>
            {
                IWebApplicationMiddleware middleware = context.Middleware.OfType<RateLimitingMiddleware>().LastOrDefault()
                    ?? new RateLimitingBuilder().Build();

                return exchange => middleware.InvokeAsync(exchange, next);
            });
        }
    }
}
