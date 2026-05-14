using System;

namespace Assimalign.Cohesion.Dns;

/// <summary>
/// Boolean flag bits carried in the 16-bit flags word of the DNS header (RFC 1035
/// &#167; 4.1.1, RFC 4035 &#167; 3.2 for the DNSSEC additions).
/// </summary>
/// <remarks>
/// The flag word also encodes <see cref="DnsOpCode"/> (4 bits) and <see cref="DnsResponseCode"/>
/// (4 bits) which are NOT part of this enum &#8211; they have dedicated properties on
/// <see cref="DnsHeader"/>. <see cref="DnsHeaderFlags"/> only covers the single-bit flags.
/// </remarks>
[Flags]
public enum DnsHeaderFlags : ushort
{
    /// <summary>No flags set.</summary>
    None = 0,

    /// <summary>QR &#8211; query / response indicator. Set on responses, cleared on queries.</summary>
    Response = 1 << 15,

    /// <summary>AA &#8211; authoritative answer. Set in responses from a server that is
    /// authoritative for the queried zone.</summary>
    AuthoritativeAnswer = 1 << 10,

    /// <summary>TC &#8211; truncated. The response was truncated because it didn't fit the
    /// payload size negotiated for the transport.</summary>
    Truncated = 1 << 9,

    /// <summary>RD &#8211; recursion desired. Set in queries that ask the server to recurse.</summary>
    RecursionDesired = 1 << 8,

    /// <summary>RA &#8211; recursion available. Set in responses from a server that supports
    /// recursive queries.</summary>
    RecursionAvailable = 1 << 7,

    /// <summary>AD &#8211; authentic data (DNSSEC, RFC 4035 §3.2). Set in responses whose
    /// data the validator considers authentic.</summary>
    AuthenticData = 1 << 5,

    /// <summary>CD &#8211; checking disabled (DNSSEC, RFC 4035 §3.2). Set in queries that
    /// ask the server to suppress DNSSEC validation.</summary>
    CheckingDisabled = 1 << 4,
}
