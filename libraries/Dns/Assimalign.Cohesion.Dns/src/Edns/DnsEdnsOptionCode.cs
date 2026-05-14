namespace Assimalign.Cohesion.Dns;

/// <summary>
/// EDNS option codes. Values are assigned by IANA in the
/// <see href="https://www.iana.org/assignments/dns-parameters/dns-parameters.xhtml#dns-parameters-11">
/// DNS Parameters &#8211; EDNS0 Option Codes</see> registry.
/// </summary>
public enum DnsEdnsOptionCode : ushort
{
    /// <summary>NSID (RFC 5001) &#8211; opaque DNS server identifier returned with responses.</summary>
    Nsid = 3,

    /// <summary>DAU (RFC 6975) &#8211; DNSSEC algorithm understood by the client.</summary>
    DnssecAlgorithmUnderstood = 5,

    /// <summary>DHU (RFC 6975) &#8211; DS hash algorithm understood by the client.</summary>
    DsHashUnderstood = 6,

    /// <summary>N3U (RFC 6975) &#8211; NSEC3 hash algorithm understood by the client.</summary>
    Nsec3HashUnderstood = 7,

    /// <summary>Client subnet (RFC 7871) &#8211; ECS for geo-tailored answers.</summary>
    ClientSubnet = 8,

    /// <summary>Expire (RFC 7314) &#8211; zone-expire metadata for AXFR/IXFR responses.</summary>
    Expire = 9,

    /// <summary>Cookie (RFC 7873) &#8211; lightweight transaction binding.</summary>
    Cookie = 10,

    /// <summary>Edns-Tcp-Keepalive (RFC 7828).</summary>
    TcpKeepalive = 11,

    /// <summary>Padding (RFC 7830).</summary>
    Padding = 12,

    /// <summary>Chain query (RFC 7901).</summary>
    Chain = 13,

    /// <summary>Key tag (RFC 8145).</summary>
    KeyTag = 14,

    /// <summary>Extended DNS error (RFC 8914).</summary>
    ExtendedError = 15,

    /// <summary>Client tag (draft).</summary>
    ClientTag = 16,

    /// <summary>Server tag (draft).</summary>
    ServerTag = 17,

    /// <summary>Report channel (RFC 9567).</summary>
    ReportChannel = 18,
}
