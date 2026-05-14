using Assimalign.Cohesion.Dns.Internal;

namespace Assimalign.Cohesion.Dns;

/// <summary>
/// A pointer (<c>PTR</c>) record &#8211; RFC 1035 &#167; 3.3.12. Used for reverse DNS
/// lookups (e.g. <c>1.2.3.4.in-addr.arpa PTR host.example.com</c>). The RDATA is a single
/// domain name; subject to compression.
/// </summary>
public sealed class DnsPtrRecord : DnsRecord
{
    /// <summary>
    /// Initializes a new <c>PTR</c> record.
    /// </summary>
    /// <param name="name">The owner name (typically a reverse-mapped address).</param>
    /// <param name="pointerName">The name the owner points at.</param>
    /// <param name="timeToLive">The TTL in seconds.</param>
    /// <param name="class">The DNS class. Defaults to <see cref="DnsClass.IN"/>.</param>
    public DnsPtrRecord(DnsName name, DnsName pointerName, uint timeToLive, DnsClass @class = DnsClass.IN)
        : base(name, DnsRecordType.PTR, @class, timeToLive)
    {
        PointerName = pointerName;
    }

    /// <summary>The name this record points at.</summary>
    public DnsName PointerName { get; }

    /// <inheritdoc />
    internal override void WriteRData(ref DnsWireWriter writer)
        => DnsNameEncoder.Write(ref writer, PointerName);

    internal static DnsPtrRecord ReadRData(
        DnsName name,
        DnsClass @class,
        uint ttl,
        ref DnsWireReader reader,
        System.ReadOnlySpan<byte> message,
        int rdataStart,
        ushort rdLength)
    {
        DnsName target = DnsNameDecoder.Read(ref reader, message);
        DnsCnameRecord.EnsureFullyConsumed(reader, rdataStart, rdLength, nameof(DnsPtrRecord));
        return new DnsPtrRecord(name, target, ttl, @class);
    }
}
