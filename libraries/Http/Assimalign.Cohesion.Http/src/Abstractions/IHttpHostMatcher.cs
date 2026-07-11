namespace Assimalign.Cohesion.Http;

/// <summary>
/// A precompiled host allowlist: decides whether a request's effective host (the
/// transport-resolved <see cref="HttpHost"/>) matches a fixed set of host patterns.
/// </summary>
/// <remarks>
/// <para>
/// Implementations are created once from their patterns (see
/// <see cref="HttpHostMatcher.Create(System.Collections.Generic.IEnumerable{string})"/>) and are
/// immutable and thread-safe afterwards — matching a request is a pure comparison against the
/// precomputed pattern set, with no per-request parsing of the patterns, no regular
/// expressions, and no reflection.
/// </para>
/// <para>
/// This is a <em>validation</em> primitive — "is this host one of mine?" — used for
/// allowed-hosts enforcement (a defense against Host-header injection). Host-based route
/// <em>selection</em> ("which endpoint serves this host?") is the Web routing layer's job;
/// both operate on the same <see cref="HttpHost"/> component semantics but answer different
/// questions.
/// </para>
/// </remarks>
public interface IHttpHostMatcher
{
    /// <summary>
    /// Gets a value indicating whether the matcher accepts any host (it was created from the
    /// <c>*</c> match-any pattern), making <see cref="IsMatch"/> unconditionally
    /// <see langword="true"/>.
    /// </summary>
    bool IsMatchAny { get; }

    /// <summary>
    /// Determines whether the supplied host matches the allowlist.
    /// </summary>
    /// <param name="host">The request host to test, as resolved by the transport.</param>
    /// <returns>
    /// <see langword="true"/> when the host's normalized host component (port ignored,
    /// IPv6 brackets ignored, case-insensitive) matches an exact pattern or falls under a
    /// wildcard-subdomain pattern, or when the matcher is match-any; otherwise
    /// <see langword="false"/>. A host that is not a well-formed <c>host[:port]</c> never
    /// matches a non-match-any allowlist.
    /// </returns>
    bool IsMatch(HttpHost host);
}
