using Assimalign.Cohesion.Dns.Internal;

namespace Assimalign.Cohesion.Dns;

/// <summary>
/// A canonical-name (<c>CNAME</c>) record &#8211; RFC 1035 &#167; 3.3.1. The RDATA is a
/// single domain name; subject to compression.
/// </summary>
public sealed class DnsCnameRecord : DnsRecord
{
    /// <summary>
    /// Initializes a new <c>CNAME</c> record.
    /// </summary>
    /// <param name="name">The owner name (the alias).</param>
    /// <param name="canonicalName">The canonical name the alias resolves to.</param>
    /// <param name="timeToLive">The TTL in seconds.</param>
    /// <param name="class">The DNS class. Defaults to <see cref="DnsClass.IN"/>.</param>
    public DnsCnameRecord(DnsName name, DnsName canonicalName, uint timeToLive, DnsClass @class = DnsClass.IN)
        : base(name, DnsRecordType.CNAME, @class, timeToLive)
    {
        CanonicalName = canonicalName;
    }

    /// <summary>The canonical name pointed to by this alias.</summary>
    public DnsName CanonicalName { get; }

    /// <inheritdoc />
    internal override void WriteRData(ref DnsWireWriter writer)
        => DnsNameEncoder.Write(ref writer, CanonicalName);

    internal static DnsCnameRecord ReadRData(
        DnsName name,
        DnsClass @class,
        uint ttl,
        ref DnsWireReader reader,
        System.ReadOnlySpan<byte> message,
        int rdataStart,
        ushort rdLength)
    {
        DnsName target = DnsNameDecoder.Read(ref reader, message);
        EnsureFullyConsumed(reader, rdataStart, rdLength, nameof(DnsCnameRecord));
        return new DnsCnameRecord(name, target, ttl, @class);
    }

    internal static void EnsureFullyConsumed(
        DnsWireReader reader,
        int rdataStart,
        ushort rdLength,
        string recordTypeName)
    {
        int consumed = reader.Position - rdataStart;
        if (consumed != rdLength)
        {
            DnsException.ThrowMalformed(
                $"{recordTypeName} RDATA: declared length {rdLength} but consumed {consumed} octets");
        }
    }
}
