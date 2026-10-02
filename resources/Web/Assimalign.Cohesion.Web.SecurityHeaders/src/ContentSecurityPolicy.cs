using System;
using System.Collections.Generic;

using Assimalign.Cohesion.Web.SecurityHeaders.Internal;

namespace Assimalign.Cohesion.Web.SecurityHeaders;

/// <summary>
/// An immutable, validated Content Security Policy (W3C Content Security Policy Level 3), emitted by the
/// security-headers middleware as <c>Content-Security-Policy</c>
/// (<see cref="SecurityHeadersPolicy.ContentSecurityPolicy"/>) or
/// <c>Content-Security-Policy-Report-Only</c> (<see cref="SecurityHeadersPolicy.ContentSecurityPolicyReportOnly"/>).
/// </summary>
/// <remarks>
/// <para>
/// Build one with <see cref="Create"/> or a <see cref="ContentSecurityPolicyBuilder"/>. The serialized
/// form is computed once, when the security-headers policy is compiled; a policy with nonce sources is
/// rendered per response by substituting the exchange's <see cref="ISecurityHeadersFeature.Nonce"/>.
/// </para>
/// <para>
/// A typical nonce-based ("strict") policy:
/// <code>
/// ContentSecurityPolicy.Create(csp =&gt; csp
///     .DefaultSrc(sources =&gt; sources.Self())
///     .ScriptSrc(sources =&gt; sources.Nonce().StrictDynamic())
///     .ObjectSrc(sources =&gt; sources.None())
///     .BaseUri(sources =&gt; sources.None()));
/// </code>
/// </para>
/// </remarks>
public sealed class ContentSecurityPolicy
{
    private readonly ContentSecurityPolicyDirective[] _directives;
    private readonly ContentSecurityPolicyTemplate _template;

    internal ContentSecurityPolicy(ContentSecurityPolicyDirective[] directives)
    {
        _directives = directives;
        _template = ContentSecurityPolicyTemplate.Create(directives, appendedDirective: null);
    }

    /// <summary>
    /// Gets whether the policy carries a nonce source, so each response needs the exchange's nonce.
    /// </summary>
    public bool UsesNonce => _template.UsesNonce;

    internal IReadOnlyList<ContentSecurityPolicyDirective> Directives => _directives;

    /// <summary>
    /// Builds a policy with a <see cref="ContentSecurityPolicyBuilder"/>.
    /// </summary>
    /// <param name="configure">The callback that sets the policy's directives.</param>
    /// <returns>The policy.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="configure"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">A directive or source is not valid CSP.</exception>
    /// <exception cref="InvalidOperationException">The callback set no directive.</exception>
    public static ContentSecurityPolicy Create(Action<ContentSecurityPolicyBuilder> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);

        ContentSecurityPolicyBuilder builder = new();
        configure(builder);
        return builder.Build();
    }

    /// <summary>
    /// Returns the serialized policy, with each nonce source shown as <c>'nonce-{nonce}'</c>.
    /// </summary>
    /// <returns>The policy in its header form, for display and diagnostics.</returns>
    public override string ToString() => _template.ToString();
}
