using System;
using System.Linq;

using Assimalign.Cohesion.Web.SecurityHeaders.Internal;

namespace Assimalign.Cohesion.Web.SecurityHeaders;

/// <summary>
/// Pipeline-builder members that place the browser security-headers middleware in a web application's
/// pipeline.
/// </summary>
public static class SecurityHeadersExtensions
{
    extension(IWebApplicationPipelineBuilder builder)
    {
        /// <summary>
        /// Places the middleware that stages the security headers on every response that passes through it:
        /// the middleware the application registered with <c>builder.Services.AddSecurityHeaders(headers =&gt; ...)</c>,
        /// or one with the safe defaults when it registered none: <c>X-Content-Type-Options: nosniff</c>,
        /// <c>Content-Security-Policy: frame-ancestors 'none'</c> with <c>X-Frame-Options: DENY</c>, and
        /// <c>Referrer-Policy: strict-origin-when-cross-origin</c>.
        /// </summary>
        /// <returns>The same pipeline builder for chaining.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="builder"/> is <see langword="null"/>.</exception>
        /// <remarks>
        /// <para>
        /// The policy is configured where it is registered (<see cref="SecurityHeadersBuilder"/>); this verb only
        /// positions it. When the application registered the headers more than once, the last registration
        /// wins.
        /// </para>
        /// <para>
        /// <b>Place it at the front</b>, after <c>UseHttpLogging</c> and ahead of the exception boundary
        /// (<c>UseErrorHandling</c>), static files, and <c>UseRouting</c>; it reads no client identity, so it
        /// may also precede <c>UseForwardedHeaders</c>. A response a middleware ahead of it writes never
        /// passes through it. The headers are staged through <see cref="IWebResponseStartingFeature"/>
        /// when the head is about to commit, after any exception boundary has reset the response, so
        /// error pages carry them. Custom servers must publish this feature. Endpoint overrides
        /// (<see cref="SecurityHeadersMetadata"/>) still apply from that position, because the endpoint is
        /// known by the time the headers are staged.
        /// </para>
        /// <para>
        /// A field already present when the headers are staged was set by the application and is kept,
        /// unless <see cref="SecurityHeadersPolicy.OverwriteExistingHeaders"/> is set. Handlers read the
        /// per-request Content Security Policy nonce from <see cref="ISecurityHeadersFeature"/>.
        /// </para>
        /// </remarks>
        public IWebApplicationPipelineBuilder UseSecurityHeaders()
        {
            ArgumentNullException.ThrowIfNull(builder);

            return builder.Use(static (context, next) =>
            {
                IWebApplicationMiddleware middleware = context.Middleware.OfType<SecurityHeadersMiddleware>().LastOrDefault()
                    ?? new SecurityHeadersBuilder().Build();

                return exchange => middleware.InvokeAsync(exchange, next);
            });
        }
    }
}
