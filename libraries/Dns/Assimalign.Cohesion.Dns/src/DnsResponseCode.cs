namespace Assimalign.Cohesion.Dns;

/// <summary>
/// DNS response codes (RCODE) returned by a responder. Carried in the four-bit <c>RCODE</c>
/// field of the DNS header and extended to twelve bits by the EDNS OPT pseudo-record (RFC 6891).
/// Values are assigned by IANA in the
/// <see href="https://www.iana.org/assignments/dns-parameters/dns-parameters.xhtml#dns-parameters-6">
/// DNS Parameters &#8211; RCODEs</see> registry.
/// </summary>
public enum DnsResponseCode : ushort
{
    /// <summary>No error condition. RFC 1035.</summary>
    NoError = 0,

    /// <summary>The name server was unable to interpret the query. RFC 1035.</summary>
    FormErr = 1,

    /// <summary>The name server was unable to process the query due to a problem with the name server. RFC 1035.</summary>
    ServFail = 2,

    /// <summary>The domain name referenced in the query does not exist. RFC 1035.</summary>
    NXDomain = 3,

    /// <summary>The name server does not support the requested kind of query. RFC 1035.</summary>
    NotImp = 4,

    /// <summary>The name server refuses to perform the specified operation for policy reasons. RFC 1035.</summary>
    Refused = 5,

    /// <summary>Name exists when it should not. RFC 2136.</summary>
    YXDomain = 6,

    /// <summary>RR set exists when it should not. RFC 2136.</summary>
    YXRRSet = 7,

    /// <summary>RR set that should exist does not. RFC 2136.</summary>
    NXRRSet = 8,

    /// <summary>Server is not authoritative for zone, or not authorized. RFC 2136 / RFC 2845.</summary>
    NotAuth = 9,

    /// <summary>Name not contained in zone. RFC 2136.</summary>
    NotZone = 10,

    /// <summary>DSO-TYPE not implemented. RFC 8490.</summary>
    DsoTypeNI = 11,

    // ---- Extended RCODEs (require EDNS OPT, RFC 6891) ----------------------

    /// <summary>Bad OPT version or TSIG signature failure. RFC 6891 / RFC 2845.</summary>
    BadVers = 16,

    /// <summary>Key not recognized. RFC 2845.</summary>
    BadKey = 17,

    /// <summary>Signature out of time window. RFC 2845.</summary>
    BadTime = 18,

    /// <summary>Bad TKEY mode. RFC 2930.</summary>
    BadMode = 19,

    /// <summary>Duplicate key name. RFC 2930.</summary>
    BadName = 20,

    /// <summary>Algorithm not supported. RFC 2930.</summary>
    BadAlg = 21,

    /// <summary>Bad truncation. RFC 4635.</summary>
    BadTrunc = 22,

    /// <summary>Bad/missing server cookie. RFC 7873.</summary>
    BadCookie = 23,
}
