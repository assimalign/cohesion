using System;
using System.Collections.Generic;
using Assimalign.Cohesion.Dns.Internal;

namespace Assimalign.Cohesion.Dns;

/// <summary>
/// The EDNS OPT pseudo-record (RFC 6891 &#167; 6). Appears in the additional section of a
/// message to negotiate payload size, surface extended RCODEs, and carry typed options
/// (ECS, Cookie, Extended-DNS-Error, &#8230;).
/// </summary>
/// <remarks>
/// <para>
/// The OPT record repurposes the fields of a regular resource record:
/// </para>
/// <list type="bullet">
///   <item><description><c>NAME</c> &#8211; must be the root (<c>.</c>).</description></item>
///   <item><description><c>CLASS</c> &#8211; reinterpreted as the <em>requestor's UDP payload size</em>.</description></item>
///   <item><description><c>TTL</c> &#8211; reinterpreted as a packed
///   (extended-RCODE &#171; 24 | version &#171; 16 | flags) word.</description></item>
///   <item><description><c>RDATA</c> &#8211; a sequence of (option-code, option-length, option-data) triples.</description></item>
/// </list>
/// <para>
/// Cohesion exposes those fields through typed properties (<see cref="UdpPayloadSize"/>,
/// <see cref="ExtendedRCodeHigh"/>, <see cref="Version"/>, <see cref="Flags"/>) but stores
/// them in the packed form expected by the wire writer.
/// </para>
/// </remarks>
public sealed class DnsOptRecord : DnsRecord
{
    /// <summary>
    /// Initializes a new OPT pseudo-record.
    /// </summary>
    /// <param name="udpPayloadSize">Maximum UDP payload size the requestor can reassemble
    /// (RFC 6891 §6.2.3). 512 is the legacy minimum; modern resolvers typically advertise
    /// 1232 to stay under typical Ethernet MTU.</param>
    /// <param name="extendedRCodeHigh">Upper 8 bits of the extended 12-bit RCODE. The low
    /// 4 bits stay in the header.</param>
    /// <param name="version">EDNS version. Always 0 in current deployments.</param>
    /// <param name="flags">EDNS flag bits (DO).</param>
    /// <param name="options">The typed options carried in the RDATA.</param>
    public DnsOptRecord(
        ushort udpPayloadSize,
        byte extendedRCodeHigh,
        byte version,
        DnsEdnsFlags flags,
        IReadOnlyList<DnsEdnsOption> options)
        : base(DnsName.Root, DnsRecordType.OPT, (DnsClass)udpPayloadSize, PackTtl(extendedRCodeHigh, version, flags))
    {
        ArgumentNullException.ThrowIfNull(options);
        UdpPayloadSize = udpPayloadSize;
        ExtendedRCodeHigh = extendedRCodeHigh;
        Version = version;
        Flags = flags;
        Options = options;
    }

    /// <summary>
    /// Convenience constructor for an OPT record with no options.
    /// </summary>
    public DnsOptRecord(ushort udpPayloadSize, DnsEdnsFlags flags = DnsEdnsFlags.None)
        : this(udpPayloadSize, 0, 0, flags, Array.Empty<DnsEdnsOption>())
    {
    }

    /// <summary>Maximum UDP payload size advertised by the requestor.</summary>
    public ushort UdpPayloadSize { get; }

    /// <summary>Upper 8 bits of the extended 12-bit RCODE.</summary>
    public byte ExtendedRCodeHigh { get; }

    /// <summary>EDNS version (always 0 in current deployments).</summary>
    public byte Version { get; }

    /// <summary>EDNS flag bits.</summary>
    public DnsEdnsFlags Flags { get; }

    /// <summary>Typed options carried in the RDATA.</summary>
    public IReadOnlyList<DnsEdnsOption> Options { get; }

    /// <inheritdoc />
    internal override void WriteRData(ref DnsWireWriter writer)
    {
        foreach (DnsEdnsOption option in Options)
        {
            writer.WriteUInt16((ushort)option.Code);
            int lengthOffset = writer.Position;
            writer.WriteUInt16(0); // placeholder for option-length
            int payloadStart = writer.Position;
            option.WritePayload(ref writer);
            int payloadLength = writer.Position - payloadStart;
            if (payloadLength > ushort.MaxValue)
            {
                DnsException.ThrowMalformed(
                    $"EDNS option {option.Code} payload exceeds 65535 octets ({payloadLength})");
            }
            writer.PatchUInt16(lengthOffset, (ushort)payloadLength);
        }
    }

    internal static DnsOptRecord ReadRData(
        DnsClass @class,
        uint ttl,
        ref DnsWireReader reader,
        ushort rdLength)
    {
        ushort udpPayloadSize = (ushort)@class;
        byte extendedRCodeHigh = (byte)((ttl >> 24) & 0xFF);
        byte version = (byte)((ttl >> 16) & 0xFF);
        DnsEdnsFlags flags = (DnsEdnsFlags)(ttl & 0xFFFF);

        var options = new List<DnsEdnsOption>();
        int rdataStart = reader.Position;
        while (reader.Position - rdataStart < rdLength)
        {
            DnsEdnsOptionCode code = (DnsEdnsOptionCode)reader.ReadUInt16();
            ushort optionLength = reader.ReadUInt16();
            int optionStart = reader.Position;

            DnsEdnsOption option = code switch
            {
                DnsEdnsOptionCode.ClientSubnet => DnsEdnsClientSubnetOption.ReadPayload(ref reader, optionLength),
                DnsEdnsOptionCode.Cookie => DnsEdnsCookieOption.ReadPayload(ref reader, optionLength),
                DnsEdnsOptionCode.ExtendedError => DnsEdnsExtendedErrorOption.ReadPayload(ref reader, optionLength),
                _ => new DnsEdnsUnknownOption(code, reader.ReadBytes(optionLength)),
            };

            int consumed = reader.Position - optionStart;
            if (consumed != optionLength)
            {
                DnsException.ThrowMalformed(
                    $"EDNS option {code}: declared length {optionLength} but consumed {consumed} octets");
            }
            options.Add(option);
        }

        if (reader.Position - rdataStart != rdLength)
        {
            DnsException.ThrowMalformed("OPT record RDATA length mismatch");
        }

        return new DnsOptRecord(udpPayloadSize, extendedRCodeHigh, version, flags, options);
    }

    private static uint PackTtl(byte extendedRCodeHigh, byte version, DnsEdnsFlags flags)
        => ((uint)extendedRCodeHigh << 24) | ((uint)version << 16) | (ushort)flags;
}
