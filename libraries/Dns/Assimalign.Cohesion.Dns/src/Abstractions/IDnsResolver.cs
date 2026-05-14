using System;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Dns;

/// <summary>
/// A <em>recursive</em> DNS resolver: given a question, walks the delegation chain (starting
/// from the root or from a configured forwarder) until it produces an authoritative answer.
/// </summary>
/// <remarks>
/// <para>
/// Every <see cref="IDnsResolver"/> is also an <see cref="IDnsClient"/> &#8211; the resolver
/// shape adds two capabilities on top of the basic query:
/// </para>
/// <list type="bullet">
///   <item><description><see cref="ResolveAsync"/> performs the recursive walk and may return
///   a long chain of <see cref="DnsMessage"/> values traversed to reach the answer.</description></item>
///   <item><description><see cref="ClearCacheAsync"/> drops any cached records held by the
///   resolver, useful for testing and for operator-triggered cache flushes.</description></item>
/// </list>
/// <para>
/// Caching policy (TTL, negative caching, QNAME minimization) is implementation-defined and
/// lives behind the resolver's options bag.
/// </para>
/// </remarks>
public interface IDnsResolver : IDnsClient
{
    /// <summary>
    /// Resolves <paramref name="question"/> by walking the delegation chain. The returned
    /// message is the authoritative answer for the question; intermediate referrals are
    /// consumed internally.
    /// </summary>
    Task<DnsMessage> ResolveAsync(DnsQuestion question, CancellationToken cancellationToken = default);

    /// <summary>
    /// Clears every cached record held by the resolver. Implementations that don't cache may
    /// implement this as a no-op.
    /// </summary>
    Task ClearCacheAsync(CancellationToken cancellationToken = default);
}
