using System;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Dns;

/// <summary>
/// The thin caller-facing surface for asking a DNS question. An <see cref="IDnsClient"/>
/// sends one query and returns one response without taking a position on whether the answer
/// came from a cache, a recursive resolver, or an upstream authority.
/// </summary>
/// <remarks>
/// <para>
/// Implementations live in sibling packages
/// (<c>Assimalign.Cohesion.Dns.Client</c> for the recursive resolver,
/// future Authority and stub packages for other shapes). They <strong>SHOULD</strong> raise
/// <see cref="DnsException"/> with an explicit <see cref="DnsErrorCode"/> on failure rather
/// than surfacing OS-level socket exceptions directly.
/// </para>
/// <para>
/// The contract is async-only by design; DNS is a network operation and a synchronous facade
/// would only encourage anti-patterns. Implementations <strong>MUST</strong> honour
/// <paramref name="cancellationToken"/>.
/// </para>
/// </remarks>
public interface IDnsClient : IDisposable, IAsyncDisposable
{
    /// <summary>
    /// Sends <paramref name="question"/> and returns the responder's <see cref="DnsMessage"/>.
    /// </summary>
    /// <param name="question">The question to ask.</param>
    /// <param name="cancellationToken">Cancels the in-flight query. Implementations that maintain
    /// connection pools must release any resources held for the cancelled query.</param>
    /// <returns>A task that completes with the upstream's response. Non-success RCODEs surface
    /// as <see cref="DnsException"/>; the returned message is always a successful answer.</returns>
    /// <exception cref="DnsException">The query failed; the exception's <see cref="DnsException.Code"/>
    /// indicates the category.</exception>
    Task<DnsMessage> QueryAsync(DnsQuestion question, CancellationToken cancellationToken = default);
}
