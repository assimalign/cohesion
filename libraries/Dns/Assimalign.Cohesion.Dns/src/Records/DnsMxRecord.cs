using Assimalign.Cohesion.Dns.Internal;

namespace Assimalign.Cohesion.Dns;

/// <summary>
/// A mail-exchange (<c>MX</c>) record &#8211; RFC 1035 &#167; 3.3.9. The RDATA is a
/// 16-bit preference followed by a domain name; the name is subject to compression.
/// Receivers select the MX with the lowest preference value (smaller = higher priority).
/// </summary>
public sealed class DnsMxRecord : DnsRecord
{
    /// <summary>
    /// Initializes a new <c>MX</c> record.
    /// </summary>
    /// <param name="name">The owner name (the domain whose mail this MX serves).</param>
    /// <param name="preference">Preference value; lower wins.</param>
    /// <param name="exchange">The host that handles mail for the owner.</param>
    /// <param name="timeToLive">The TTL in seconds.</param>
    /// <param name="class">The DNS class. Defaults to <see cref="DnsClass.IN"/>.</param>
    public DnsMxRecord(
        DnsName name,
        ushort preference,
        DnsName exchange,
        uint timeToLive,
        DnsClass @class = DnsClass.IN)
        : base(name, DnsRecordType.MX, @class, timeToLive)
    {
        Preference = preference;
        Exchange = exchange;
    }

    /// <summary>Preference value &#8211; lower numbers are tried first.</summary>
    public ushort Preference { get; }

    /// <summary>The host that accepts mail for the owner.</summary>
    public DnsName Exchange { get; }

    /// <inheritdoc />
    internal override void WriteRData(ref DnsWireWriter writer)
    {
        writer.WriteUInt16(Preference);
        DnsNameEncoder.Write(ref writer, Exchange);
    }

    internal static DnsMxRecord ReadRData(
        DnsName name,
        DnsClass @class,
        uint ttl,
        ref DnsWireReader reader,
        System.ReadOnlySpan<byte> message,
        int rdataStart,
        ushort rdLength)
    {
        ushort preference = reader.ReadUInt16();
        DnsName exchange = DnsNameDecoder.Read(ref reader, message);
        DnsCnameRecord.EnsureFullyConsumed(reader, rdataStart, rdLength, nameof(DnsMxRecord));
        return new DnsMxRecord(name, preference, exchange, ttl, @class);
    }
}
