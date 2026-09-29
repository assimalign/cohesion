using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Dns;

/// <summary>
/// A <em>recursive</em> DNS resolver: given a question, walks the delegation chain (starting
/// from the root or a configured forwarder) until it produces an authoritative answer. Adds
/// recursion semantics and cache management on top of the basic <see cref="DnsClient"/>
/// surface.
/// </summary>
/// <remarks>
/// <para>
/// Every <see cref="DnsResolver"/> is also a <see cref="DnsClient"/> &#8211; the resolver
/// shape adds:
/// </para>
/// <list type="bullet">
///   <item><description><see cref="ResolveAsync"/> performs the recursive walk, consuming
///   intermediate referrals internally and returning only the final authoritative answer.</description></item>
///   <item><description><see cref="ClearCacheAsync"/> drops any cached records held by the
///   resolver, useful for operator-triggered flushes and tests.</description></item>
/// </list>
/// <para>
/// Caching policy (TTL handling, negative caching, QNAME minimization) is implementation-
/// defined and lives behind the resolver's options bag.
/// </para>
/// </remarks>
public abstract class DnsResolver : DnsClient
{
    /// <summary>
    /// Resolves <paramref name="question"/> by walking the delegation chain. The returned
    /// message is the authoritative answer for the question; intermediate referrals are
    /// consumed internally.
    /// </summary>
    public abstract Task<DnsMessage> ResolveAsync(
        DnsQuestion question,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Clears every cached record held by the resolver. Implementations that don't cache may
    /// override as a no-op that returns <see cref="Task.CompletedTask"/>.
    /// </summary>
    public abstract Task ClearCacheAsync(CancellationToken cancellationToken = default);
}
