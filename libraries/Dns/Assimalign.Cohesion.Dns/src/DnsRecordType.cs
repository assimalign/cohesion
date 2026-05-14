namespace Assimalign.Cohesion.Dns;

/// <summary>
/// DNS resource-record type codes. Values are assigned by IANA in the
/// <see href="https://www.iana.org/assignments/dns-parameters/dns-parameters.xhtml#dns-parameters-4">
/// DNS Parameters &#8211; Resource Record (RR) TYPEs</see> registry.
/// </summary>
/// <remarks>
/// This enum lists the values commonly handled by Cohesion DNS providers. Unknown numeric
/// codes are still legal on the wire and are preserved as opaque RDATA by the wire-format
/// layer. Adding a named value here does not change wire semantics; it only gives callers a
/// stable C# identifier.
/// </remarks>
public enum DnsRecordType : ushort
{
    // ---- Common (RFC 1035) -------------------------------------------------

    /// <summary>Host address (IPv4). RFC 1035 &#167; 3.4.1.</summary>
    A = 1,
    /// <summary>Authoritative name server. RFC 1035 &#167; 3.3.11.</summary>
    NS = 2,
    /// <summary>Canonical name. RFC 1035 &#167; 3.3.1.</summary>
    CNAME = 5,
    /// <summary>Start of a zone authority. RFC 1035 &#167; 3.3.13.</summary>
    SOA = 6,
    /// <summary>Pointer to a canonical name (reverse DNS). RFC 1035 &#167; 3.3.12.</summary>
    PTR = 12,
    /// <summary>Host information. RFC 1035 &#167; 3.3.2.</summary>
    HINFO = 13,
    /// <summary>Mail exchange. RFC 1035 &#167; 3.3.9.</summary>
    MX = 15,
    /// <summary>Text strings. RFC 1035 &#167; 3.3.14.</summary>
    TXT = 16,

    // ---- IPv6 + service (RFC 3596, 2782) -----------------------------------

    /// <summary>Host address (IPv6). RFC 3596.</summary>
    AAAA = 28,
    /// <summary>Service locator. RFC 2782.</summary>
    SRV = 33,

    // ---- EDNS (RFC 6891) ---------------------------------------------------

    /// <summary>Option pseudo-record carrying EDNS metadata. RFC 6891.</summary>
    OPT = 41,

    // ---- DNSSEC (RFC 4034) -------------------------------------------------

    /// <summary>Delegation signer. RFC 4034.</summary>
    DS = 43,
    /// <summary>Resource record signature. RFC 4034.</summary>
    RRSIG = 46,
    /// <summary>Next secure record. RFC 4034.</summary>
    NSEC = 47,
    /// <summary>DNS public key. RFC 4034.</summary>
    DNSKEY = 48,
    /// <summary>Hashed authenticated denial of existence. RFC 5155.</summary>
    NSEC3 = 50,
    /// <summary>NSEC3 parameters. RFC 5155.</summary>
    NSEC3PARAM = 51,
    /// <summary>TLSA / DANE record. RFC 6698.</summary>
    TLSA = 52,

    // ---- Modern service records (RFC 9460) ---------------------------------

    /// <summary>Service binding. RFC 9460.</summary>
    SVCB = 64,
    /// <summary>HTTPS service binding. RFC 9460.</summary>
    HTTPS = 65,

    // ---- Other --------------------------------------------------------------

    /// <summary>Certification Authority Authorization. RFC 8659.</summary>
    CAA = 257,

    /// <summary>Query types &#8211; request for any RR (legacy). RFC 1035.</summary>
    ANY = 255,
}
