using System;

namespace Assimalign.Cohesion.Http.Internal;

/// <summary>
/// The default <see cref="HttpHostMatcher"/> implementation: exact host entries and
/// wildcard-subdomain suffixes stored as plain arrays, matched with span comparisons — no
/// regular expressions, no per-request pattern parsing, no reflection.
/// </summary>
internal sealed class HttpHostPatternMatcher : HttpHostMatcher
{
    /// <summary>
    /// The shared match-any instance (the compiled <c>*</c> pattern), surfaced publicly as
    /// <see cref="HttpHostMatcher.MatchAny"/>.
    /// </summary>
    public static HttpHostPatternMatcher Any { get; } = new();

    // Exact entries are stored normalized (trimmed, IPv6 brackets removed) by the factory;
    // wildcard entries are stored as their ".suffix" including the label-boundary dot.
    private readonly string[] _exactHosts;
    private readonly string[] _wildcardSuffixes;
    private readonly bool _isMatchAny;

    private HttpHostPatternMatcher()
    {
        _exactHosts = Array.Empty<string>();
        _wildcardSuffixes = Array.Empty<string>();
        _isMatchAny = true;
    }

    public HttpHostPatternMatcher(string[] exactHosts, string[] wildcardSuffixes)
    {
        _exactHosts = exactHosts;
        _wildcardSuffixes = wildcardSuffixes;
        _isMatchAny = false;
    }

    /// <inheritdoc />
    public override bool IsMatchAny => _isMatchAny;

    /// <inheritdoc />
    public override bool IsMatch(HttpHost host)
    {
        if (_isMatchAny)
        {
            return true;
        }

        // Match on the normalized host component: port stripped, IPv6 brackets removed,
        // whitespace trimmed. A host that is not structurally host[:port] never matches.
        if (!host.TryGetComponents(out ReadOnlySpan<char> name, out _) || name.IsEmpty)
        {
            return false;
        }

        foreach (string exact in _exactHosts)
        {
            if (name.Equals(exact.AsSpan(), StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        foreach (string suffix in _wildcardSuffixes)
        {
            // The suffix includes its leading dot, so at least one subdomain label is
            // required: ".example.com" matches "a.example.com" (and any depth) but neither
            // the apex "example.com" nor "evilexample.com".
            if (name.Length > suffix.Length && name.EndsWith(suffix.AsSpan(), StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
