using System;
using System.Text;
using Assimalign.Cohesion.Dns.Internal;

namespace Assimalign.Cohesion.Dns;

/// <summary>
/// The EDNS Extended-DNS-Error option (EDE) &#8211; RFC 8914. Carries a structured
/// info-code plus an optional UTF-8 text explanation, allowing responders to surface
/// validation, blocking, or rate-limiting reasons that the four-bit RCODE can't represent.
/// </summary>
public sealed class DnsEdnsExtendedErrorOption : DnsEdnsOption
{
    /// <summary>
    /// Initializes a new EDE option.
    /// </summary>
    /// <param name="infoCode">The IANA-assigned info code (RFC 8914 §4 + the EDE registry).</param>
    /// <param name="extraText">Optional UTF-8 explanatory text.</param>
    public DnsEdnsExtendedErrorOption(ushort infoCode, string? extraText = null)
        : base(DnsEdnsOptionCode.ExtendedError)
    {
        InfoCode = infoCode;
        ExtraText = extraText;
    }

    /// <summary>The numeric info code.</summary>
    public ushort InfoCode { get; }

    /// <summary>The UTF-8 explanatory text; null when omitted.</summary>
    public string? ExtraText { get; }

    /// <inheritdoc />
    internal override void WritePayload(ref DnsWireWriter writer)
    {
        writer.WriteUInt16(InfoCode);
        if (!string.IsNullOrEmpty(ExtraText))
        {
            byte[] bytes = Encoding.UTF8.GetBytes(ExtraText);
            writer.WriteBytes(bytes);
        }
    }

    internal static DnsEdnsExtendedErrorOption ReadPayload(ref DnsWireReader reader, int payloadLength)
    {
        if (payloadLength < 2)
        {
            DnsException.ThrowMalformed(
                $"Extended-DNS-Error option requires at least 2 octets (info code); got {payloadLength}");
        }
        ushort infoCode = reader.ReadUInt16();
        string? extra = null;
        if (payloadLength > 2)
        {
            ReadOnlySpan<byte> bytes = reader.ReadBytes(payloadLength - 2);
            extra = Encoding.UTF8.GetString(bytes);
        }
        return new DnsEdnsExtendedErrorOption(infoCode, extra);
    }
}
