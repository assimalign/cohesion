namespace Assimalign.Cohesion.Dns;

/// <summary>
/// DNS class codes. Values are assigned by IANA in the
/// <see href="https://www.iana.org/assignments/dns-parameters/dns-parameters.xhtml#dns-parameters-2">
/// DNS Parameters &#8211; CLASSes</see> registry. The wire-format layer treats <see cref="DnsClass"/>
/// as a raw <c>ushort</c>; the named values here are the ones callers typically construct.
/// </summary>
public enum DnsClass : ushort
{
    /// <summary>Internet class. RFC 1035. Used by virtually every modern DNS deployment.</summary>
    IN = 1,

    /// <summary>CSNET class (obsolete). RFC 1035.</summary>
    CS = 2,

    /// <summary>CHAOS class. Used for diagnostic queries (e.g. <c>version.bind. TXT CH</c>). RFC 1035.</summary>
    CH = 3,

    /// <summary>Hesiod class. RFC 1035.</summary>
    HS = 4,

    /// <summary>Wildcard class used in queries. RFC 1035.</summary>
    Any = 255,
}
