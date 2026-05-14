using System.Collections.Generic;

namespace Assimalign.Cohesion.Dns;

/// <summary>
/// A DNS zone &#8211; the unit of authoritative ownership in the DNS namespace. Owns every
/// record at and below its <see cref="Origin"/> down to any subordinate delegations.
/// </summary>
/// <remarks>
/// <para>
/// This contract is the public surface used by <see cref="IDnsAuthority"/>. Implementations
/// (zone files, in-memory zones, database-backed zones) live in sibling packages.
/// </para>
/// <para>
/// PR 1 ships the minimal shape: origin, serial, an enumeration handle, and the read-only
/// lookup for a specific record set. Mutating operations (zone updates, DNSSEC signing,
/// AXFR/IXFR transfers) land alongside Feature 07 in a later PR.
/// </para>
/// </remarks>
public interface IDnsZone
{
    /// <summary>The zone apex (e.g. <c>example.com.</c>).</summary>
    DnsName Origin { get; }

    /// <summary>
    /// The current <c>SERIAL</c> field of the zone's SOA record. Implementations bump this on
    /// every committed change so secondaries can detect when an AXFR/IXFR is needed.
    /// </summary>
    uint Serial { get; }

    /// <summary>
    /// Returns the records at <paramref name="name"/> matching <paramref name="type"/>. An
    /// empty enumeration means the zone is authoritative for the name but has no records of
    /// the requested type; callers can distinguish that from <c>NXDomain</c> via
    /// <see cref="Contains"/>.
    /// </summary>
    IEnumerable<DnsRecord> GetRecords(DnsName name, DnsRecordType type);

    /// <summary>
    /// True when <paramref name="name"/> sits at or below the zone origin and has at least one
    /// record (of any type) in this zone.
    /// </summary>
    bool Contains(DnsName name);
}
