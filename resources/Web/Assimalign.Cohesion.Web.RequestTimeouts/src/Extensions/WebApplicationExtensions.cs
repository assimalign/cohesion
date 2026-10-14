using System;

using Assimalign.Cohesion.Web.RequestTimeouts.Internal;

namespace Assimalign.Cohesion.Web.RequestTimeouts;

/// <summary>
/// Pipeline-builder members that add request-timeout enforcement to a web application.
/// </summary>
/// <remarks>
/// Register <c>UseRequestTimeouts</c> <b>after</b> <c>UseRouting</c> and before the long-running
/// middleware it should govern: the middleware reads the endpoint <c>UseRouting</c> published to pick
/// the endpoint's policy, then governs everything downstream of it, the endpoint included.
/// Composition is dependency-free: the options are captured at builder time and no request-time
/// service location occurs.
/// </remarks>
public static class WebApplicationExtensions
{
    extension(IWebApplicationPipelineBuilder builder)
    {
        /// <summary>
        /// Adds the request-timeout middleware to the pipeline. With no configuration the
        /// middleware applies no global timeout — only endpoints carrying
        /// <see cref="RequestTimeoutMetadata"/> (or handlers arming
        /// <see cref="IRequestTimeoutFeature.SetTimeout"/>) are governed.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Register it after <c>UseRouting</c>. The timer starts when the exchange reaches the
        /// middleware, armed with the published endpoint's <see cref="RequestTimeoutMetadata"/> policy
        /// when it carries one, which replaces the global default, and with the global default
        /// otherwise (including for requests no route matched).
        /// </para>
        /// <para>
        /// Registered ahead of <c>UseRouting</c>, the global default still governs every request, but no
        /// endpoint is known yet: an endpoint whose metadata carries a timeout then fails with an
        /// <see cref="InvalidOperationException"/> when it is dispatched instead of running unbounded,
        /// and an endpoint that disables the timeout runs under the global default.
        /// </para>
        /// </remarks>
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
        /// <remarks>
        /// Register it after <c>UseRouting</c>, so endpoints carrying
        /// <see cref="RequestTimeoutMetadata"/> replace the default with their own policy (see the
        /// configurable overload).
        /// </remarks>
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
