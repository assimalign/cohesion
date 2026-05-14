using System;
using System.Collections.Generic;

namespace Assimalign.Cohesion.Dns;

/// <summary>
/// An authoritative DNS server &#8211; owns one or more <see cref="IDnsZone"/> instances and
/// answers queries directly out of those zones rather than delegating to upstream resolvers.
/// </summary>
/// <remarks>
/// <para>
/// This contract is the public surface used by future authoritative server implementations.
/// PR 1 ships the minimal shape so the contract layer compiles; the in-process authoritative
/// server, zone-file loader, AXFR/IXFR transfers, and dynamic UPDATE handling all live in
/// later PRs under Feature 07.
/// </para>
/// <para>
/// The shape intentionally exposes only zone enumeration and lookup. The query-answering
/// surface is supplied by <see cref="IDnsClient"/>, which authoritative implementations also
/// satisfy. Splitting the two interfaces lets callers distinguish "ask the authority directly"
/// from "ask via recursive resolution".
/// </para>
/// </remarks>
public interface IDnsAuthority : IDisposable, IAsyncDisposable
{
    /// <summary>
    /// The zones served by this authority. The collection reflects the authority's current
    /// state &#8211; live-add/remove APIs land alongside Feature 07.
    /// </summary>
    IReadOnlyCollection<IDnsZone> Zones { get; }

    /// <summary>
    /// Returns the zone whose <see cref="IDnsZone.Origin"/> is the longest match for
    /// <paramref name="name"/>, or <see langword="null"/> if no zone owns it.
    /// </summary>
    IDnsZone? FindZone(DnsName name);
}
