using System;

using Assimalign.Cohesion.Http;

namespace Assimalign.Cohesion.Web.Proxy.Internal;

/// <summary>
/// One compiled host pattern of a route or a rule: its matcher, and how closely it matches.
/// </summary>
/// <param name="Matcher">The matcher compiled from the pattern alone.</param>
/// <param name="Rank">
/// How specific the pattern is: <see cref="ExactRank"/> for an exact host, one more than the
/// suffix's length for a <c>*.suffix</c> pattern, and <see cref="AnyRank"/> for <c>*</c>.
/// </param>
internal readonly record struct ProxyHostPattern(HttpHostMatcher Matcher, int Rank)
{
    /// <summary>
    /// The rank of <c>*</c>, and of a route that names no host.
    /// </summary>
    public const int AnyRank = 0;

    /// <summary>
    /// The rank of an exact host, above every wildcard.
    /// </summary>
    public const int ExactRank = int.MaxValue;

    /// <summary>
    /// Gets a value indicating whether the pattern is <c>*</c> or a <c>*.suffix</c> wildcard.
    /// </summary>
    public bool IsWildcard => Rank != ExactRank;

    /// <summary>
    /// Compiles one pattern.
    /// </summary>
    /// <param name="pattern">The pattern, not null or white space.</param>
    /// <exception cref="ArgumentException">The pattern is not valid (see <see cref="HttpHostMatcher.Create"/>).</exception>
    public static ProxyHostPattern Create(string pattern)
    {
        string trimmed = pattern.Trim();

        // HttpHostMatcher validates the pattern: a port, a malformed host or a misplaced '*' throws.
        HttpHostMatcher matcher = HttpHostMatcher.Create([trimmed]);

        int rank = trimmed switch
        {
            "*" => AnyRank,
            _ when trimmed.StartsWith("*.", StringComparison.Ordinal) => trimmed.Length,
            _ => ExactRank,
        };

        return new ProxyHostPattern(matcher, rank);
    }
}
