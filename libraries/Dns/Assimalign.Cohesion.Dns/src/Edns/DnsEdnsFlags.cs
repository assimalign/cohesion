using System;

namespace Assimalign.Cohesion.Dns;

/// <summary>
/// Boolean flag bits carried in the 16-bit flags field of the EDNS OPT pseudo-record
/// (RFC 6891 &#167; 6.1.3). Currently only the DO bit is defined; the rest are reserved for
/// future extension.
/// </summary>
[Flags]
public enum DnsEdnsFlags : ushort
{
    /// <summary>No flags set.</summary>
    None = 0,

    /// <summary>DO &#8211; DNSSEC OK (RFC 3225). Set in queries that ask the responder to
    /// include DNSSEC RRs in the answer.</summary>
    DnssecOk = 1 << 15,
}
