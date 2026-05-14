using System;
using System.Net;
using System.Net.Sockets;
using Assimalign.Cohesion.Dns.Internal;

namespace Assimalign.Cohesion.Dns;

/// <summary>
/// The EDNS Client-Subnet (ECS) option &#8211; RFC 7871. Carries a partial client address so
/// authoritative servers can tailor answers geographically without seeing the full IP.
/// </summary>
/// <remarks>
/// <para>
/// Wire layout of the option payload:
/// </para>
/// <pre>
///  0   1   2   3   4   5   6   7
/// +---+---+---+---+---+---+---+---+
/// |          FAMILY               |  (2 octets: 1 = IPv4, 2 = IPv6)
/// +---+---+---+---+---+---+---+---+
/// |   SOURCE-NETMASK              |  (1 octet)
/// +---+---+---+---+---+---+---+---+
/// |    SCOPE-NETMASK              |  (1 octet; 0 in queries)
/// +---+---+---+---+---+---+---+---+
/// |  ADDRESS  (CEIL(SOURCE/8) bytes)  |
/// +---+---+---+---+---+---+---+---+
/// </pre>
/// </remarks>
public sealed class DnsEdnsClientSubnetOption : DnsEdnsOption
{
    /// <summary>
    /// Initializes a new <c>edns-client-subnet</c> option.
    /// </summary>
    /// <param name="address">The address whose prefix is shared with the upstream.</param>
    /// <param name="sourcePrefixLength">Number of significant bits in <paramref name="address"/>.</param>
    /// <param name="scopePrefixLength">Number of significant bits the responder considered.
    /// Typically 0 in queries and populated by the responder in answers.</param>
    public DnsEdnsClientSubnetOption(IPAddress address, byte sourcePrefixLength, byte scopePrefixLength = 0)
        : base(DnsEdnsOptionCode.ClientSubnet)
    {
        ArgumentNullException.ThrowIfNull(address);
        int maxBits = address.AddressFamily == AddressFamily.InterNetwork ? 32 : 128;
        if (sourcePrefixLength > maxBits || scopePrefixLength > maxBits)
        {
            throw new ArgumentException(
                $"ECS prefix length must be 0..{maxBits} for {address.AddressFamily}.");
        }
        Address = address;
        SourcePrefixLength = sourcePrefixLength;
        ScopePrefixLength = scopePrefixLength;
    }

    /// <summary>The (truncated) client address.</summary>
    public IPAddress Address { get; }

    /// <summary>Source prefix length in bits.</summary>
    public byte SourcePrefixLength { get; }

    /// <summary>Scope prefix length in bits, set by responders.</summary>
    public byte ScopePrefixLength { get; }

    /// <summary>The numeric family code (1 = IPv4, 2 = IPv6).</summary>
    public ushort Family => Address.AddressFamily == AddressFamily.InterNetwork ? (ushort)1 : (ushort)2;

    /// <inheritdoc />
    internal override void WritePayload(ref DnsWireWriter writer)
    {
        writer.WriteUInt16(Family);
        writer.WriteUInt8(SourcePrefixLength);
        writer.WriteUInt8(ScopePrefixLength);

        // Address bytes — only ceil(prefix/8) of them per RFC 7871.
        int addrSize = Address.AddressFamily == AddressFamily.InterNetwork ? 4 : 16;
        Span<byte> addrBuffer = stackalloc byte[16];
        if (!Address.TryWriteBytes(addrBuffer, out int written) || written != addrSize)
        {
            DnsException.ThrowMalformed("failed to serialize ECS address");
        }
        int bytesToWrite = (SourcePrefixLength + 7) / 8;
        writer.WriteBytes(addrBuffer.Slice(0, bytesToWrite));
    }

    internal static DnsEdnsClientSubnetOption ReadPayload(ref DnsWireReader reader, int payloadLength)
    {
        if (payloadLength < 4)
        {
            DnsException.ThrowMalformed("ECS option payload must be at least 4 octets");
        }

        int start = reader.Position;
        ushort family = reader.ReadUInt16();
        byte source = reader.ReadUInt8();
        byte scope = reader.ReadUInt8();

        int addrBytes = payloadLength - 4;
        int expectedFullSize = family == 1 ? 4 : 16;
        if (addrBytes > expectedFullSize)
        {
            DnsException.ThrowMalformed(
                $"ECS option carries {addrBytes} address octets but family {family} only allows {expectedFullSize}");
        }

        Span<byte> addressBuffer = stackalloc byte[16];
        addressBuffer.Clear();
        ReadOnlySpan<byte> bytes = reader.ReadBytes(addrBytes);
        bytes.CopyTo(addressBuffer);

        IPAddress address = family switch
        {
            1 => new IPAddress(addressBuffer.Slice(0, 4)),
            2 => new IPAddress(addressBuffer.Slice(0, 16)),
            _ => throw NotSupportedFamily(family),
        };

        int consumed = reader.Position - start;
        if (consumed != payloadLength)
        {
            DnsException.ThrowMalformed(
                $"ECS option: declared length {payloadLength} but consumed {consumed} octets");
        }

        return new DnsEdnsClientSubnetOption(address, source, scope);

        static Exception NotSupportedFamily(ushort family)
        {
            DnsException.ThrowMalformed($"ECS option uses unsupported family code {family}");
            return null!; // unreachable
        }
    }
}
