using System;
using System.Collections.Generic;
using System.Text;

namespace Assimalign.Cohesion.Web.SecurityHeaders.Internal;

/// <summary>
/// A serialized policy precomputed at builder time, split around its nonce sources. A policy without a
/// nonce source is a single constant string; a nonce-bearing policy is rendered per response by joining
/// its segments with the exchange's nonce, so each <c>'nonce-…'</c> source carries the same value.
/// </summary>
internal sealed class ContentSecurityPolicyTemplate
{
    /// <summary>The text shown in place of the nonce by <see cref="ToString"/>.</summary>
    internal const string NoncePlaceholder = "{nonce}";

    private readonly string[] _segments;

    private ContentSecurityPolicyTemplate(string[] segments)
    {
        _segments = segments;
    }

    /// <summary>Gets whether the policy carries at least one nonce source.</summary>
    public bool UsesNonce => _segments.Length > 1;

    /// <summary>
    /// Serializes the directives, in order, as one policy, optionally followed by
    /// <paramref name="appendedDirective"/> (the <c>frame-ancestors</c> directive the framing policy owns).
    /// </summary>
    public static ContentSecurityPolicyTemplate Create(IReadOnlyList<ContentSecurityPolicyDirective> directives, string? appendedDirective)
    {
        List<string> segments = new(1);
        StringBuilder current = new();

        foreach (ContentSecurityPolicyDirective directive in directives)
        {
            if (current.Length > 0 || segments.Count > 0)
            {
                current.Append("; ");
            }

            current.Append(directive.Name);

            if (directive.Sources is { } sources)
            {
                foreach (string? source in sources)
                {
                    current.Append(' ');

                    if (source is null)
                    {
                        // CSP3 nonce-source = "'nonce-" base64-value "'": cut a segment where the value goes.
                        current.Append("'nonce-");
                        segments.Add(current.ToString());
                        current.Clear();
                        current.Append('\'');
                        continue;
                    }

                    current.Append(source);
                }
            }
            else if (!string.IsNullOrEmpty(directive.Value))
            {
                current.Append(' ').Append(directive.Value);
            }
        }

        if (appendedDirective is not null)
        {
            if (current.Length > 0 || segments.Count > 0)
            {
                current.Append("; ");
            }

            current.Append(appendedDirective);
        }

        segments.Add(current.ToString());
        return new ContentSecurityPolicyTemplate(segments.ToArray());
    }

    /// <summary>Creates a template holding a single directive, for a policy that is only that directive.</summary>
    public static ContentSecurityPolicyTemplate FromDirective(string directive)
    {
        return new ContentSecurityPolicyTemplate([directive]);
    }

    /// <summary>
    /// Renders the policy for one response, substituting the exchange's nonce into every nonce source.
    /// </summary>
    /// <exception cref="InvalidOperationException">The nonce is not a CSP <c>base64-value</c>.</exception>
    public string Render(ISecurityHeadersFeature feature)
    {
        if (_segments.Length == 1)
        {
            return _segments[0];
        }

        string nonce = feature.Nonce;
        if (nonce is null || !SecurityHeadersGrammar.IsBase64Value(nonce))
        {
            throw new InvalidOperationException(
                "The Content Security Policy nonce supplied by the installed ISecurityHeadersFeature is not a CSP base64-value " +
                "(ALPHA / DIGIT / '+' / '/' / '-' / '_', up to two '=' of padding), so it cannot be written into a header.");
        }

        return string.Join(nonce, _segments);
    }

    /// <summary>Returns the policy with each nonce shown as <c>'nonce-{nonce}'</c>.</summary>
    public override string ToString() => string.Join(NoncePlaceholder, _segments);
}
