using System;

using Assimalign.Cohesion.Web.RequestTimeouts.Internal;

namespace Assimalign.Cohesion.Web.RequestTimeouts;

/// <summary>
/// Pipeline-builder members that add request-timeout enforcement to a web application.
/// </summary>
/// <remarks>
/// Register <c>UseRequestTimeouts</c> <b>before</b> <c>UseRouting</c> (and before any other
/// long-running middleware it should govern): the middleware wraps everything downstream of it,
/// and it applies per-endpoint policies by observing the router publish its match — which only
/// works when routing runs inside the timeout scope. Composition is dependency-free: the options
/// are captured at builder time and no request-time service location occurs.
/// </remarks>
public static class WebApplicationExtensions
{
    extension(IWebApplicationPipelineBuilder builder)
    {
        /// <summary>
        /// Adds the request-timeout middleware to the pipeline. With no configuration the
        /// middleware applies no global timeout — only endpoints carrying
        /// <see cref="RequestTimeoutMetadata"/> (or handlers arming
        /// <see cref="IHttpRequestTimeoutFeature.SetTimeout"/>) are governed.
        /// </summary>
        /// <param name="configure">An optional callback to configure the middleware.</param>
        /// <returns>The same pipeline builder for chaining.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="builder"/> is <see langword="null"/>.</exception>
        public IWebApplicationPipelineBuilder UseRequestTimeouts(Action<RequestTimeoutOptions>? configure = null)
        {
            ArgumentNullException.ThrowIfNull(builder);

            RequestTimeoutOptions options = new();
            configure?.Invoke(options);

            return builder.Use(new RequestTimeoutMiddleware(options));
        }

        /// <summary>
        /// Adds the request-timeout middleware with a global default timeout, answered with the
        /// default 504 status when it fires.
        /// </summary>
        /// <param name="defaultTimeout">The time any request may execute before it is timed out.</param>
        /// <returns>The same pipeline builder for chaining.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="builder"/> is <see langword="null"/>.</exception>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="defaultTimeout"/> is zero or negative.</exception>
        public IWebApplicationPipelineBuilder UseRequestTimeouts(TimeSpan defaultTimeout)
        {
            return builder.UseRequestTimeouts(options =>
                options.DefaultPolicy = new RequestTimeoutPolicy { Timeout = defaultTimeout });
        }
    }
}
