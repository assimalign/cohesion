using System;

namespace Assimalign.Cohesion.Dns;

/// <summary>
/// A DNS resource record &#8211; the unit of data exchanged by every DNS protocol operation
/// (RFC 1035 &#167; 3.2). Carries a name, a type, a class, a time-to-live, and an opaque
/// <em>RDATA</em> payload whose interpretation depends on <see cref="Type"/>.
/// </summary>
/// <remarks>
/// <para>
/// PR 1 ships only the public shape so the contract layer compiles. The strongly-typed RDATA
/// hierarchy (A / AAAA / CNAME / MX / SOA / etc.) lands in PR 2 alongside the wire-format
/// implementation. Until then, callers can hold a <see cref="DnsRecord"/> reference but its
/// <see cref="Data"/> is an opaque byte buffer in wire encoding.
/// </para>
/// </remarks>
public class DnsRecord
{
    /// <summary>
    /// Initializes a new <see cref="DnsRecord"/>.
    /// </summary>
    /// <param name="name">The owner name.</param>
    /// <param name="type">The record type.</param>
    /// <param name="class">The DNS class (almost always <see cref="DnsClass.IN"/>).</param>
    /// <param name="timeToLive">The TTL in seconds.</param>
    /// <param name="data">The wire-format RDATA bytes.</param>
    public DnsRecord(
        DnsName name,
        DnsRecordType type,
        DnsClass @class,
        uint timeToLive,
        ReadOnlyMemory<byte> data)
    {
        Name = name;
        Type = type;
        Class = @class;
        TimeToLive = timeToLive;
        Data = data;
    }

    /// <summary>The owner name (the name the record describes).</summary>
    public DnsName Name { get; }

    /// <summary>The record type.</summary>
    public DnsRecordType Type { get; }

    /// <summary>The DNS class.</summary>
    public DnsClass Class { get; }

    /// <summary>The time-to-live in seconds.</summary>
    public uint TimeToLive { get; }

    /// <summary>The wire-format RDATA bytes. Strongly-typed accessors land in PR 2.</summary>
    public ReadOnlyMemory<byte> Data { get; }
}
