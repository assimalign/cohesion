using System;
using Assimalign.Cohesion.Dns.Internal;

namespace Assimalign.Cohesion.Dns;

/// <summary>
/// A DNS resource record &#8211; the unit of data exchanged by every DNS protocol operation
/// (RFC 1035 &#167; 3.2). Carries a name, a type, a class, a time-to-live, and (in derived
/// types) a strongly-typed payload whose interpretation depends on <see cref="Type"/>.
/// </summary>
/// <remarks>
/// <para>
/// Derived types expose strongly-typed properties for the well-known record families
/// (A, AAAA, CNAME, MX, TXT, NS, SOA, PTR, SRV, OPT). Any record whose type code isn't
/// recognized parses as a <see cref="DnsUnknownRecord"/> preserving the opaque RDATA bytes
/// so the message can round-trip without loss.
/// </para>
/// <para>
/// Records are immutable. The wire-format reader / writer in <see cref="DnsMessage"/>
/// dispatches on <see cref="Type"/> to instantiate the right concrete type, so consumers can
/// pattern-match (<c>switch</c> expressions) against the known shapes.
/// </para>
/// </remarks>
public abstract class DnsRecord
{
    /// <summary>
    /// Initializes shared properties on a derived record.
    /// </summary>
    protected DnsRecord(DnsName name, DnsRecordType type, DnsClass @class, uint timeToLive)
    {
        Name = name;
        Type = type;
        Class = @class;
        TimeToLive = timeToLive;
    }

    /// <summary>The owner name (the name the record describes).</summary>
    public DnsName Name { get; }

    /// <summary>The record type.</summary>
    public DnsRecordType Type { get; }

    /// <summary>The DNS class.</summary>
    public DnsClass Class { get; }

    /// <summary>The time-to-live in seconds.</summary>
    public uint TimeToLive { get; }

    /// <summary>
    /// Writes the RDATA portion of this record to <paramref name="writer"/>. Called by
    /// <see cref="DnsMessage"/> after the common header (name + type + class + TTL +
    /// RDLENGTH placeholder) has been emitted.
    /// </summary>
    /// <remarks>
    /// Implementations write only the type-specific RDATA bytes. The framing (RDLENGTH
    /// patch-up) is handled by the message-level serializer to keep record code focused on
    /// the protocol-specific content.
    /// </remarks>
    internal abstract void WriteRData(ref DnsWireWriter writer);
}
