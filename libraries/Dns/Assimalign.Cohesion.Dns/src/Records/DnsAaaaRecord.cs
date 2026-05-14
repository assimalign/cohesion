using System;
using System.Net;
using Assimalign.Cohesion.Dns.Internal;

namespace Assimalign.Cohesion.Dns;

/// <summary>
/// An IPv6 host-address (<c>AAAA</c>) record &#8211; RFC 3596. The RDATA is exactly 16 octets
/// of network-order IPv6 address.
/// </summary>
public sealed class DnsAaaaRecord : DnsRecord
{
    /// <summary>
    /// Initializes a new <c>AAAA</c> record.
    /// </summary>
    /// <param name="name">The owner name.</param>
    /// <param name="address">The IPv6 address. Must be an
    /// <see cref="System.Net.Sockets.AddressFamily.InterNetworkV6"/> address.</param>
    /// <param name="timeToLive">The TTL in seconds.</param>
    /// <param name="class">The DNS class. Defaults to <see cref="DnsClass.IN"/>.</param>
    public DnsAaaaRecord(DnsName name, IPAddress address, uint timeToLive, DnsClass @class = DnsClass.IN)
        : base(name, DnsRecordType.AAAA, @class, timeToLive)
    {
        ArgumentNullException.ThrowIfNull(address);
        if (address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetworkV6)
        {
            throw new ArgumentException("AAAA records require an IPv6 (AddressFamily.InterNetworkV6) address.", nameof(address));
        }
        Address = address;
    }

    /// <summary>The IPv6 address recorded in this entry.</summary>
    public IPAddress Address { get; }

    /// <inheritdoc />
    internal override void WriteRData(ref DnsWireWriter writer)
    {
        Span<byte> bytes = stackalloc byte[16];
        if (!Address.TryWriteBytes(bytes, out int written) || written != 16)
        {
            DnsException.ThrowMalformed("failed to serialize IPv6 address into 16 octets");
        }
        writer.WriteBytes(bytes);
    }

    internal static DnsAaaaRecord ReadRData(
        DnsName name,
        DnsClass @class,
        uint ttl,
        ref DnsWireReader reader,
        ushort rdLength)
    {
        if (rdLength != 16)
        {
            DnsException.ThrowMalformed($"AAAA record RDATA length must be 16 octets; got {rdLength}");
        }
        ReadOnlySpan<byte> bytes = reader.ReadBytes(16);
        return new DnsAaaaRecord(name, new IPAddress(bytes), ttl, @class);
    }
}
