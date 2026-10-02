using System;
using System.Collections.Generic;

using Assimalign.Cohesion.Web.SecurityHeaders.Internal;

namespace Assimalign.Cohesion.Web.SecurityHeaders;

/// <summary>
/// Clickjacking protection: which documents may embed the response in a frame. One policy drives both
/// fields that express it, the Content Security Policy <c>frame-ancestors</c> directive (W3C CSP Level 3)
/// and the legacy <c>X-Frame-Options</c> field (RFC 7034), so the two can never disagree.
/// </summary>
/// <remarks>
/// <para>
/// User agents that implement <c>frame-ancestors</c> ignore <c>X-Frame-Options</c> when an enforced
/// policy carries the directive (HTML Standard, "check a navigation response's adherence to
/// X-Frame-Options"), so the legacy field only matters to older user agents. It is emitted when it can
/// say the same thing: <c>DENY</c> for <see cref="Deny"/>, <c>SAMEORIGIN</c> for <see cref="SameOrigin"/>,
/// and nothing for a list of other ancestors, which <c>X-Frame-Options</c> cannot express (its
/// <c>ALLOW-FROM</c> form is obsolete and ignored by current user agents).
/// </para>
/// <para>
/// The <c>frame-ancestors</c> directive is appended to the enforced <c>Content-Security-Policy</c> field:
/// on its own when <see cref="SecurityHeadersPolicy.ContentSecurityPolicy"/> is not set, after the
/// configured directives when it is.
/// </para>
/// </remarks>
public sealed class FramingPolicy
{
    private FramingPolicy(string frameAncestors, string? xFrameOptions)
    {
        FrameAncestors = frameAncestors;
        XFrameOptions = xFrameOptions;
    }

    /// <summary>
    /// Gets the policy that forbids every frame: <c>frame-ancestors 'none'</c> and
    /// <c>X-Frame-Options: DENY</c>. The default.
    /// </summary>
    public static FramingPolicy Deny { get; } = new("'none'", "DENY");

    /// <summary>
    /// Gets the policy that allows frames from the same origin only: <c>frame-ancestors 'self'</c> and
    /// <c>X-Frame-Options: SAMEORIGIN</c>.
    /// </summary>
    public static FramingPolicy SameOrigin { get; } = new("'self'", "SAMEORIGIN");

    /// <summary>
    /// Gets the <c>frame-ancestors</c> source list this policy emits, for example <c>'none'</c>.
    /// </summary>
    public string FrameAncestors { get; }

    /// <summary>
    /// Gets the <c>X-Frame-Options</c> value this policy emits (<c>DENY</c> or <c>SAMEORIGIN</c>), or
    /// <see langword="null"/> when the allowed ancestors cannot be expressed in that field.
    /// </summary>
    public string? XFrameOptions { get; }

    /// <summary>
    /// Creates a policy that allows the listed ancestors to frame the response.
    /// </summary>
    /// <param name="ancestorSources">
    /// One or more CSP <c>ancestor-source</c> expressions: <c>'self'</c>, a scheme source such as
    /// <c>https:</c>, or a host source such as <c>https://partner.example.com</c> or
    /// <c>https://*.example.com</c>.
    /// </param>
    /// <returns>
    /// The policy. A list that is exactly <c>'self'</c> returns <see cref="SameOrigin"/>; any other list
    /// emits no <c>X-Frame-Options</c> field.
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="ancestorSources"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="ancestorSources"/> is empty, or an entry is not an <c>ancestor-source</c>
    /// (<c>'none'</c> included: use <see cref="Deny"/>).
    /// </exception>
    public static FramingPolicy AllowFrom(params string[] ancestorSources)
    {
        ArgumentNullException.ThrowIfNull(ancestorSources);

        if (ancestorSources.Length == 0)
        {
            throw new ArgumentException("Name at least one ancestor source; use FramingPolicy.Deny to forbid every frame.", nameof(ancestorSources));
        }

        List<string> sources = new(ancestorSources.Length);
        foreach (string source in ancestorSources)
        {
            if (source is null || !SecurityHeadersGrammar.IsAncestorSource(source))
            {
                throw new ArgumentException(
                    $"'{source}' is not a frame-ancestors source; use 'self', a scheme source (https:), or a host source " +
                    "(https://partner.example.com). Use FramingPolicy.Deny for 'none'.",
                    nameof(ancestorSources));
            }

            string normalized = source.Equals("'self'", StringComparison.OrdinalIgnoreCase) ? "'self'" : source;
            if (!sources.Contains(normalized))
            {
                sources.Add(normalized);
            }
        }

        if (sources.Count == 1 && sources[0] == "'self'")
        {
            return SameOrigin;
        }

        return new FramingPolicy(string.Join(' ', sources), xFrameOptions: null);
    }
}
