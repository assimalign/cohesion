using Assimalign.Cohesion.Dns.Internal;

namespace Assimalign.Cohesion.Dns;

/// <summary>
/// An authoritative-name-server (<c>NS</c>) record &#8211; RFC 1035 &#167; 3.3.11. The RDATA
/// is a single domain name pointing at the authoritative server for the zone; subject to
/// compression.
/// </summary>
public sealed class DnsNsRecord : DnsRecord
{
    /// <summary>
    /// Initializes a new <c>NS</c> record.
    /// </summary>
    /// <param name="name">The owner name (the zone).</param>
    /// <param name="nameServer">The authoritative name server for the zone.</param>
    /// <param name="timeToLive">The TTL in seconds.</param>
    /// <param name="class">The DNS class. Defaults to <see cref="DnsClass.IN"/>.</param>
    public DnsNsRecord(DnsName name, DnsName nameServer, uint timeToLive, DnsClass @class = DnsClass.IN)
        : base(name, DnsRecordType.NS, @class, timeToLive)
    {
        NameServer = nameServer;
    }

    /// <summary>The authoritative name server for the owner zone.</summary>
    public DnsName NameServer { get; }

    /// <inheritdoc />
    internal override void WriteRData(ref DnsWireWriter writer)
        => DnsNameEncoder.Write(ref writer, NameServer);

    internal static DnsNsRecord ReadRData(
        DnsName name,
        DnsClass @class,
        uint ttl,
        ref DnsWireReader reader,
        System.ReadOnlySpan<byte> message,
        int rdataStart,
        ushort rdLength)
    {
        DnsName target = DnsNameDecoder.Read(ref reader, message);
        DnsCnameRecord.EnsureFullyConsumed(reader, rdataStart, rdLength, nameof(DnsNsRecord));
        return new DnsNsRecord(name, target, ttl, @class);
    }
}
