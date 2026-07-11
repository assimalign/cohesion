using System;
using System.Collections.Generic;

namespace Assimalign.Cohesion.Http;

using Assimalign.Cohesion.Http.Internal;

/// <summary>
/// Creates <see cref="IHttpHostMatcher"/> instances from host allowlist patterns.
/// </summary>
/// <remarks>
/// <para>
/// Three pattern forms are supported, mirroring the Web routing host-constraint grammar so
/// host validation and host-based route selection read the same way:
/// </para>
/// <list type="bullet">
///   <item><description><c>*</c> — match any host.</description></item>
///   <item><description>
///   An exact host: a name (<c>example.com</c>), an IPv4 literal (<c>127.0.0.1</c>), or an
///   IPv6 literal in bracketed (<c>[::1]</c>) or unbracketed (<c>::1</c>) form — the two IPv6
///   spellings are equivalent. Comparison is case-insensitive.
///   </description></item>
///   <item><description>
///   A wildcard subdomain: <c>*.example.com</c> matches <c>api.example.com</c> and
///   <c>a.b.example.com</c> (any depth) but not <c>example.com</c> itself and not
///   <c>evilexample.com</c> — the label boundary is required.
///   </description></item>
/// </list>
/// <para>
/// Patterns match the <em>host component only</em>: the port of an incoming host is ignored,
/// and a pattern carrying a port (<c>example.com:8080</c>) is rejected at creation rather than
/// silently never matching. IPv6 literals are compared textually (bracket-insensitive,
/// case-insensitive); address canonicalization is deliberately not performed — write the form
/// clients actually send.
/// </para>
/// </remarks>
public static class HttpHostMatcher
{
    /// <summary>
    /// Gets the matcher that accepts any host (the precompiled <c>*</c> pattern).
    /// </summary>
    public static IHttpHostMatcher MatchAny { get; } = HttpHostPatternMatcher.MatchAny;

    /// <summary>
    /// Creates a matcher from the supplied allowlist patterns, precompiling them once.
    /// </summary>
    /// <param name="patterns">
    /// The allowlist patterns: exact hosts, <c>*.suffix</c> wildcard subdomains, or <c>*</c>
    /// for match-any. At least one pattern is required — to accept every host, either pass
    /// <c>*</c> explicitly or apply no host filtering at all; an empty allowlist would silently
    /// reject every request and is treated as a configuration error.
    /// </param>
    /// <returns>The precompiled matcher. When any pattern is <c>*</c>, the match-any matcher is returned.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="patterns"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="patterns"/> is empty, or a pattern is empty or whitespace, carries a
    /// port, is not a well-formed host, or misuses the <c>*</c> wildcard (anywhere other than
    /// a whole-pattern <c>*</c> or a leading <c>*.</c> label).
    /// </exception>
    public static IHttpHostMatcher Create(IEnumerable<string> patterns)
    {
        ArgumentNullException.ThrowIfNull(patterns);

        bool matchAny = false;
        List<string> exactHosts = new();
        List<string> wildcardSuffixes = new();

        int count = 0;
        foreach (string pattern in patterns)
        {
            count++;

            if (string.IsNullOrWhiteSpace(pattern))
            {
                throw new ArgumentException(
                    "A host allowlist pattern must not be null, empty, or whitespace.",
                    nameof(patterns));
            }

            string trimmed = pattern.Trim();

            if (trimmed == "*")
            {
                // Match-any dominates, but keep validating the remaining patterns so a typo
                // beside the wildcard still fails loudly at creation.
                matchAny = true;
                continue;
            }

            // Normalize through the same component split requests go through: strips IPv6
            // brackets (so "[::1]" and "::1" compile to the same entry) and surfaces a port,
            // which allowlist patterns must not carry.
            HttpHost parsed = new(trimmed);
            if (!parsed.TryGetComponents(out ReadOnlySpan<char> host, out int? port) || host.IsEmpty)
            {
                throw new ArgumentException(
                    $"The host allowlist pattern '{pattern}' is not a well-formed host.",
                    nameof(patterns));
            }

            if (port is not null)
            {
                throw new ArgumentException(
                    $"The host allowlist pattern '{pattern}' carries a port. Host filtering matches the host component only; remove the ':{port}'.",
                    nameof(patterns));
            }

            if (host.StartsWith("*.", StringComparison.Ordinal))
            {
                // "*.example.com" -> store the ".example.com" suffix; the label boundary (the
                // leading dot) is part of the stored suffix so "evilexample.com" cannot match.
                ReadOnlySpan<char> suffix = host[1..];
                if (suffix.Length <= 1 || suffix[1..].IndexOf('*') >= 0)
                {
                    throw new ArgumentException(
                        $"The host allowlist pattern '{pattern}' is not a valid wildcard: expected '*.suffix' with a non-empty suffix and no further '*'.",
                        nameof(patterns));
                }

                wildcardSuffixes.Add(suffix.ToString());
            }
            else if (host.IndexOf('*') >= 0)
            {
                throw new ArgumentException(
                    $"The host allowlist pattern '{pattern}' misuses '*': the wildcard is only valid as the whole pattern ('*') or as a leading '*.' label.",
                    nameof(patterns));
            }
            else
            {
                exactHosts.Add(host.ToString());
            }
        }

        if (count == 0)
        {
            throw new ArgumentException(
                "At least one host allowlist pattern is required. To accept every host, pass '*' or apply no host filtering.",
                nameof(patterns));
        }

        if (matchAny)
        {
            return HttpHostPatternMatcher.MatchAny;
        }

        return new HttpHostPatternMatcher(exactHosts.ToArray(), wildcardSuffixes.ToArray());
    }
}
