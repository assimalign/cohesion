using System;
using Assimalign.Cohesion.Dns.Internal;

namespace Assimalign.Cohesion.Dns;

/// <summary>
/// Base class for an EDNS option carried inside a <see cref="DnsOptRecord"/>'s RDATA
/// (RFC 6891 &#167; 6.1.2). Each option has a 16-bit code and a length-prefixed payload.
/// </summary>
/// <remarks>
/// <para>
/// Derived types expose strongly-typed properties for the well-known options
/// (<see cref="DnsEdnsClientSubnetOption"/>, <see cref="DnsEdnsExtendedErrorOption"/>,
/// <see cref="DnsEdnsCookieOption"/>, ...). Any option whose code isn't recognized parses
/// as <see cref="DnsEdnsUnknownOption"/> preserving the opaque bytes so the OPT record can
/// round-trip without loss &#8211; required by RFC 6891 &#167; 4 for forward-compatibility.
/// </para>
/// </remarks>
public abstract class DnsEdnsOption
{
    /// <summary>
    /// Initializes shared properties on a derived option.
    /// </summary>
    protected DnsEdnsOption(DnsEdnsOptionCode code)
    {
        Code = code;
    }

    /// <summary>The IANA-assigned option code.</summary>
    public DnsEdnsOptionCode Code { get; }

    /// <summary>
    /// Writes the option's payload (the bytes that follow the 4-octet option-code +
    /// option-length header). Implementations write only the type-specific payload.
    /// </summary>
    internal abstract void WritePayload(ref DnsWireWriter writer);
}
