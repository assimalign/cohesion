using System;

using Assimalign.Cohesion.Web.SecurityHeaders.Internal;

namespace Assimalign.Cohesion.Web.SecurityHeaders;

/// <summary>
/// Configures the security-headers middleware that <c>builder.Services.AddSecurityHeaders(headers =&gt; ...)</c>
/// registers and <see cref="SecurityHeadersExtensions.UseSecurityHeaders(IWebApplicationPipelineBuilder)"/>
/// places.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="Options"/> starts from the safe defaults. Configure it to opt into a Content Security Policy
/// (with per-request nonces), a Permissions Policy and the cross-origin isolation fields, to change the
/// framing or referrer policy, or to turn a default off.
/// </para>
/// <para>
/// <see cref="Build"/> captures a copy of <see cref="Options"/> and compiles it into the field values the
/// middleware emits, so an invalid value fails where the application registers the headers, never per
/// request, and a reference to <see cref="Options"/> kept after registration cannot change the middleware.
/// </para>
/// </remarks>
public sealed class SecurityHeadersBuilder
{
    /// <summary>
    /// Initializes a builder with the safe defaults: <c>X-Content-Type-Options: nosniff</c>, clickjacking
    /// protection (<c>Content-Security-Policy: frame-ancestors 'none'</c> and <c>X-Frame-Options: DENY</c>),
    /// and <c>Referrer-Policy: strict-origin-when-cross-origin</c>. Every other field is opt-in.
    /// </summary>
    public SecurityHeadersBuilder()
    {
    }

    /// <summary>
    /// Gets the policy the middleware emits: the defaults to keep, change or turn off, the opt-in fields,
    /// and whether a field the application set is overwritten.
    /// </summary>
    public SecurityHeadersPolicy Options { get; } = new();

    /// <summary>
    /// Builds the security-headers middleware from a copy of <see cref="Options"/>.
    /// </summary>
    /// <returns>
    /// The middleware, which stages the policy's fields on every response that passes through it when the
    /// response head is about to commit.
    /// </returns>
    /// <exception cref="ArgumentOutOfRangeException">
    /// An enumeration property of <see cref="Options"/> holds an undefined value.
    /// </exception>
    public IWebApplicationMiddleware Build()
    {
        // A copy, so a reference the configuration callback kept cannot reconfigure the middleware, or the
        // endpoint adjustments it derives from this policy, after registration.
        return new SecurityHeadersMiddleware(new SecurityHeadersPolicy(Options));
    }
}
