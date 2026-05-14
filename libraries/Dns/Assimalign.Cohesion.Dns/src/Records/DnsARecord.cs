using System;
using System.Net;
using Assimalign.Cohesion.Dns.Internal;

namespace Assimalign.Cohesion.Dns;

/// <summary>
/// An IPv4 host-address (<c>A</c>) record &#8211; RFC 1035 &#167; 3.4.1. The RDATA is exactly
/// four octets of network-order IPv4 address.
/// </summary>
public sealed class DnsARecord : DnsRecord
{
    /// <summary>
    /// Initializes a new <c>A</c> record.
    /// </summary>
    /// <param name="name">The owner name.</param>
    /// <param name="address">The IPv4 address. Must be an
    /// <see cref="System.Net.Sockets.AddressFamily.InterNetwork"/> address.</param>
    /// <param name="timeToLive">The TTL in seconds.</param>
    /// <param name="class">The DNS class. Defaults to <see cref="DnsClass.IN"/>.</param>
    public DnsARecord(DnsName name, IPAddress address, uint timeToLive, DnsClass @class = DnsClass.IN)
        : base(name, DnsRecordType.A, @class, timeToLive)
    {
        ArgumentNullException.ThrowIfNull(address);
        if (address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
        {
            throw new ArgumentException("A records require an IPv4 (AddressFamily.InterNetwork) address.", nameof(address));
        }
        Address = address;
    }

    /// <summary>The IPv4 address recorded in this entry.</summary>
    public IPAddress Address { get; }

    /// <inheritdoc />
    internal override void WriteRData(ref DnsWireWriter writer)
    {
        Span<byte> bytes = stackalloc byte[4];
        if (!Address.TryWriteBytes(bytes, out int written) || written != 4)
        {
            DnsException.ThrowMalformed("failed to serialize IPv4 address into 4 octets");
        }
        writer.WriteBytes(bytes);
    }

    internal static DnsARecord ReadRData(
        DnsName name,
        DnsClass @class,
        uint ttl,
        ref DnsWireReader reader,
        ushort rdLength)
    {
        if (rdLength != 4)
        {
            DnsException.ThrowMalformed($"A record RDATA length must be 4 octets; got {rdLength}");
        }
        ReadOnlySpan<byte> bytes = reader.ReadBytes(4);
        return new DnsARecord(name, new IPAddress(bytes), ttl, @class);
    }
}
