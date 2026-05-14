namespace Assimalign.Cohesion.Dns;

/// <summary>
/// DNS operation codes. Carried in the four <c>OPCODE</c> bits of the DNS header (RFC 1035
/// &#167; 4.1.1). Values are assigned by IANA in the
/// <see href="https://www.iana.org/assignments/dns-parameters/dns-parameters.xhtml#dns-parameters-5">
/// DNS Parameters &#8211; OpCodes</see> registry.
/// </summary>
public enum DnsOpCode : byte
{
    /// <summary>Standard query. RFC 1035.</summary>
    Query = 0,

    /// <summary>Inverse query (obsoleted by RFC 3425).</summary>
    InverseQuery = 1,

    /// <summary>Server status request. RFC 1035.</summary>
    Status = 2,

    /// <summary>Asynchronous change notification from primary to secondaries. RFC 1996.</summary>
    Notify = 4,

    /// <summary>Dynamic update. RFC 2136.</summary>
    Update = 5,

    /// <summary>DNS stateful operations. RFC 8490.</summary>
    DnsStatefulOperations = 6,
}
