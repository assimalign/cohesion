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
/// credentials, and the middleware answers it before any of them runs. The policies come from
/// <c>builder.Services.AddCors(...)</c>, which builds and validates them when the application is composed;
/// <c>UseCors</c> takes no configuration of its own.
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
        /// The policies are the ones <c>builder.Services.AddCors(...)</c> registered as the application's
        /// <c>cors</c> setting. The policy for a request is the endpoint's <see cref="CorsMetadata"/>
        /// (<c>RequireCors</c>, <c>DisableCors</c>), else the default policy
        /// (<see cref="CorsOptions.AddDefaultPolicy(CorsPolicy)"/>). With neither, the middleware leaves the
        /// request alone. Without <c>AddCors</c>, only endpoints that carry an inline policy are governed.
        /// The default policy is read on each request, so a value an operator surface sets on the setting
        /// applies from the next request.
        /// </para>
        /// <para>
        /// Register it after <c>UseRouting</c>. Registered ahead of it, the middleware sees no endpoint and
        /// applies the default policy to every request, and an endpoint that declares
        /// <see cref="CorsMetadata"/> fails with an <see cref="InvalidOperationException"/> when it is
        /// dispatched, instead of running under the wrong policy.
        /// </para>
        /// <para>
        /// Actual-response headers are applied through <see cref="IWebResponseStartingFeature"/> when the
        /// head commits, for buffered and streamed responses. An exception boundary on either side of
        /// <c>UseCors</c> may reset the response without losing its CORS headers. Custom servers must
        /// publish the response-starting feature, as the default Web.Hosting server does. Register
        /// <c>UseCors</c> before <c>UseOutputCache</c>, so cache hits register this callback too.
        /// </para>
        /// </remarks>
        /// <returns>The same pipeline builder, for chaining.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="builder"/> is <see langword="null"/>.</exception>
        public IWebApplicationPipelineBuilder UseCors()
        {
            ArgumentNullException.ThrowIfNull(builder);

            return builder.Use(static (context, next) =>
            {
                var middleware = new CorsMiddleware(CorsSetting.Find(context));
                return exchange => middleware.InvokeAsync(exchange, next);
            });
        }
    }
}
