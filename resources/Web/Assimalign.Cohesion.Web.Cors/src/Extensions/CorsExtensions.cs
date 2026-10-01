using System;

using Assimalign.Cohesion.Web.Cors.Internal;

namespace Assimalign.Cohesion.Web.Cors;

/// <summary>
/// Pipeline-builder members that add cross-origin resource sharing (CORS) to a web application.
/// </summary>
/// <remarks>
/// Register <c>UseCors</c> after <c>UseRouting</c>, so the request's endpoint and its
/// <see cref="CorsMetadata"/> are known when it runs, and ahead of every middleware that can reject a
/// preflight (authentication challenges, authorization, rate limiting, antiforgery): a preflight carries no
/// credentials, and the middleware answers it before any of them runs. Composition is dependency-free: the
/// policies are built and validated when <c>UseCors</c> is called, and no request-time service location
/// occurs.
/// </remarks>
public static class CorsExtensions
{
    extension(IWebApplicationPipelineBuilder builder)
    {
        /// <summary>
        /// Adds the CORS middleware to the pipeline. It answers CORS preflights (<c>204 No Content</c>, with
        /// the policy's grant or with no CORS headers when the policy denies the request) and adds the CORS
        /// headers to the responses its policy governs.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The policy for a request is the endpoint's <see cref="CorsMetadata"/> (<c>RequireCors</c>,
        /// <c>DisableCors</c>), else the default policy (<see cref="CorsOptions.AddDefaultPolicy(CorsPolicy)"/>).
        /// With neither, the middleware leaves the request alone. With no configuration at all, only endpoints
        /// that carry an inline policy are governed.
        /// </para>
        /// <para>
        /// Register it after <c>UseRouting</c>. Registered ahead of it, the middleware sees no endpoint and
        /// applies the default policy to every request, and an endpoint that declares
        /// <see cref="CorsMetadata"/> fails with an <see cref="InvalidOperationException"/> when it is
        /// dispatched, instead of running under the wrong policy.
        /// </para>
        /// <para>
        /// The CORS headers are written before the rest of the pipeline runs and written again after it
        /// returns, unless the response has started streaming. A response that a middleware registered
        /// after <c>UseCors</c> resets and rewrites (an exception boundary, a request timeout) therefore keeps
        /// them. One that a middleware registered before <c>UseCors</c> rewrites does not.
        /// </para>
        /// </remarks>
        /// <param name="configure">An optional callback that registers the default policy and named policies.</param>
        /// <returns>The same pipeline builder, for chaining.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="builder"/> is <see langword="null"/>.</exception>
        /// <exception cref="ArgumentException"><paramref name="configure"/> adds an invalid origin, method or header name.</exception>
        /// <exception cref="InvalidOperationException">
        /// <paramref name="configure"/> registers an invalid policy, a second default policy, or a duplicate
        /// policy name.
        /// </exception>
        public IWebApplicationPipelineBuilder UseCors(Action<CorsOptions>? configure = null)
        {
            ArgumentNullException.ThrowIfNull(builder);

            CorsOptions options = new();
            configure?.Invoke(options);

            return builder.Use(new CorsMiddleware(options));
        }
    }
}
