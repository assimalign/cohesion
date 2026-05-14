using Assimalign.Cohesion.Dns.Internal;

namespace Assimalign.Cohesion.Dns;

/// <summary>
/// A start-of-authority (<c>SOA</c>) record &#8211; RFC 1035 &#167; 3.3.13. Exactly one
/// instance exists at the apex of every zone, carrying the primary nameserver, the contact
/// mailbox, the zone serial, and four timers controlling zone-transfer cadence.
/// </summary>
/// <remarks>
/// The RDATA layout is:
/// <list type="number">
///   <item><description><c>MNAME</c> &#8211; primary master name server (subject to
///   compression).</description></item>
///   <item><description><c>RNAME</c> &#8211; responsible-person mailbox, with the local part
///   represented as a label (e.g. <c>hostmaster.example.com</c> stands for
///   <c>hostmaster@example.com</c>). Subject to compression.</description></item>
///   <item><description><c>SERIAL</c>, <c>REFRESH</c>, <c>RETRY</c>, <c>EXPIRE</c>,
///   <c>MINIMUM</c> &#8211; five 32-bit unsigned integers.</description></item>
/// </list>
/// </remarks>
public sealed class DnsSoaRecord : DnsRecord
{
    /// <summary>
    /// Initializes a new <c>SOA</c> record.
    /// </summary>
    public DnsSoaRecord(
        DnsName name,
        DnsName primaryNameServer,
        DnsName responsibleMailbox,
        uint serial,
        uint refreshInterval,
        uint retryInterval,
        uint expireLimit,
        uint minimumTtl,
        uint timeToLive,
        DnsClass @class = DnsClass.IN)
        : base(name, DnsRecordType.SOA, @class, timeToLive)
    {
        PrimaryNameServer = primaryNameServer;
        ResponsibleMailbox = responsibleMailbox;
        Serial = serial;
        RefreshInterval = refreshInterval;
        RetryInterval = retryInterval;
        ExpireLimit = expireLimit;
        MinimumTtl = minimumTtl;
    }

    /// <summary>Primary master name server for the zone.</summary>
    public DnsName PrimaryNameServer { get; }

    /// <summary>Responsible-person mailbox in DNS label form.</summary>
    public DnsName ResponsibleMailbox { get; }

    /// <summary>Zone serial; bumped on every committed change.</summary>
    public uint Serial { get; }

    /// <summary>Seconds between zone-refresh checks by secondaries.</summary>
    public uint RefreshInterval { get; }

    /// <summary>Seconds before a secondary retries after a failed refresh.</summary>
    public uint RetryInterval { get; }

    /// <summary>Seconds after which a secondary should consider the zone authoritative
    /// data stale and stop serving it.</summary>
    public uint ExpireLimit { get; }

    /// <summary>Minimum TTL for records returned from this zone (RFC 2308 reinterprets this
    /// as the negative-caching TTL).</summary>
    public uint MinimumTtl { get; }

    /// <inheritdoc />
    internal override void WriteRData(ref DnsWireWriter writer)
    {
        DnsNameEncoder.Write(ref writer, PrimaryNameServer);
        DnsNameEncoder.Write(ref writer, ResponsibleMailbox);
        writer.WriteUInt32(Serial);
        writer.WriteUInt32(RefreshInterval);
        writer.WriteUInt32(RetryInterval);
        writer.WriteUInt32(ExpireLimit);
        writer.WriteUInt32(MinimumTtl);
    }

    internal static DnsSoaRecord ReadRData(
        DnsName name,
        DnsClass @class,
        uint ttl,
        ref DnsWireReader reader,
        System.ReadOnlySpan<byte> message,
        int rdataStart,
        ushort rdLength)
    {
        DnsName mname = DnsNameDecoder.Read(ref reader, message);
        DnsName rname = DnsNameDecoder.Read(ref reader, message);
        uint serial = reader.ReadUInt32();
        uint refresh = reader.ReadUInt32();
        uint retry = reader.ReadUInt32();
        uint expire = reader.ReadUInt32();
        uint minimum = reader.ReadUInt32();
        DnsCnameRecord.EnsureFullyConsumed(reader, rdataStart, rdLength, nameof(DnsSoaRecord));
        return new DnsSoaRecord(name, mname, rname, serial, refresh, retry, expire, minimum, ttl, @class);
    }
}
