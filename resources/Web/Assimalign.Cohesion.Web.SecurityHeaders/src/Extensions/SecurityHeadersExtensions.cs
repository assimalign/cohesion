using System;

using Assimalign.Cohesion.Web.SecurityHeaders.Internal;

namespace Assimalign.Cohesion.Web.SecurityHeaders;

/// <summary>
/// Pipeline-builder members that add browser security headers to a web application's responses.
/// </summary>
public static class SecurityHeadersExtensions
{
    extension(IWebApplicationPipelineBuilder builder)
    {
        /// <summary>
        /// Adds middleware that stages the security headers on every response that passes through it. With
        /// no configuration it emits the safe defaults: <c>X-Content-Type-Options: nosniff</c>,
        /// <c>Content-Security-Policy: frame-ancestors 'none'</c> with <c>X-Frame-Options: DENY</c>, and
        /// <c>Referrer-Policy: strict-origin-when-cross-origin</c>.
        /// </summary>
        /// <param name="configure">
        /// An optional callback that adjusts the policy, starting from the safe defaults: opt into a
        /// Content Security Policy (with per-request nonces), Permissions-Policy and the cross-origin
        /// isolation fields, change the framing or referrer policy, or turn a default off. The policy is
        /// captured when this method returns.
        /// </param>
        /// <returns>The same pipeline builder for chaining.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="builder"/> is <see langword="null"/>.</exception>
        /// <exception cref="ArgumentOutOfRangeException">An enumeration property of the policy holds an undefined value.</exception>
        /// <remarks>
        /// <para>
        /// <b>Register it at the front</b>, after <c>UseHttpLogging</c> and ahead of the exception boundary
        /// (<c>UseErrorHandling</c>), static files, and <c>UseRouting</c>; it reads no client identity, so it
        /// may also precede <c>UseForwardedHeaders</c>. A response a middleware ahead of it writes never
        /// passes through it,
        /// and the headers are staged when the response head is about to commit, after an inner exception
        /// boundary has reset the response, so error pages carry them. Endpoint overrides
        /// (<see cref="SecurityHeadersMetadata"/>) still apply from that position, because the endpoint is
        /// known by the time the headers are staged.
        /// </para>
        /// <para>
        /// A field already present when the headers are staged was set by the application and is kept,
        /// unless <see cref="SecurityHeadersPolicy.OverwriteExistingHeaders"/> is set. Handlers read the
        /// per-request Content Security Policy nonce from <see cref="ISecurityHeadersFeature"/>.
        /// </para>
        /// </remarks>
        public IWebApplicationPipelineBuilder UseSecurityHeaders(Action<SecurityHeadersPolicy>? configure = null)
        {
            ArgumentNullException.ThrowIfNull(builder);

            SecurityHeadersPolicy policy = new();
            configure?.Invoke(policy);

            // A copy, so a reference the callback kept cannot reconfigure the pipeline after composition.
            return builder.Use(new SecurityHeadersMiddleware(new SecurityHeadersPolicy(policy)));
        }
    }
}
