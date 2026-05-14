using Assimalign.Cohesion.Dns.Internal;

namespace Assimalign.Cohesion.Dns;

/// <summary>
/// A service-locator (<c>SRV</c>) record &#8211; RFC 2782. Carries
/// (priority, weight, port, target) for a named service. The target name is NOT subject to
/// compression per RFC 2782 §4 (but many implementations compress it anyway; the decoder
/// accepts compressed names and the encoder emits uncompressed names to stay strict).
/// </summary>
public sealed class DnsSrvRecord : DnsRecord
{
    /// <summary>
    /// Initializes a new <c>SRV</c> record.
    /// </summary>
    public DnsSrvRecord(
        DnsName name,
        ushort priority,
        ushort weight,
        ushort port,
        DnsName target,
        uint timeToLive,
        DnsClass @class = DnsClass.IN)
        : base(name, DnsRecordType.SRV, @class, timeToLive)
    {
        Priority = priority;
        Weight = weight;
        Port = port;
        Target = target;
    }

    /// <summary>Lower priorities are tried first (smaller wins).</summary>
    public ushort Priority { get; }

    /// <summary>Relative weight for load-balancing across equal-priority records.</summary>
    public ushort Weight { get; }

    /// <summary>The TCP/UDP port the service runs on.</summary>
    public ushort Port { get; }

    /// <summary>The host that provides the service.</summary>
    public DnsName Target { get; }

    /// <inheritdoc />
    internal override void WriteRData(ref DnsWireWriter writer)
    {
        writer.WriteUInt16(Priority);
        writer.WriteUInt16(Weight);
        writer.WriteUInt16(Port);
        // RFC 2782 §4 forbids compression of the SRV target. Use the uncompressed encoder so
        // signed / canonical-form records produce reproducible bytes.
        DnsNameEncoder.WriteUncompressed(ref writer, Target);
    }

    internal static DnsSrvRecord ReadRData(
        DnsName name,
        DnsClass @class,
        uint ttl,
        ref DnsWireReader reader,
        System.ReadOnlySpan<byte> message,
        int rdataStart,
        ushort rdLength)
    {
        ushort priority = reader.ReadUInt16();
        ushort weight = reader.ReadUInt16();
        ushort port = reader.ReadUInt16();
        DnsName target = DnsNameDecoder.Read(ref reader, message);
        DnsCnameRecord.EnsureFullyConsumed(reader, rdataStart, rdLength, nameof(DnsSrvRecord));
        return new DnsSrvRecord(name, priority, weight, port, target, ttl, @class);
    }
}
