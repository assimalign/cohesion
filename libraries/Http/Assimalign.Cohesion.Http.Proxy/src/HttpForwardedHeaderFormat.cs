namespace Assimalign.Cohesion.Http;

/// <summary>
/// Selects the header family a forwarder writes when it adds a forwarded hop.
/// </summary>
public enum HttpForwardedHeaderFormat
{
    /// <summary>
    /// The de facto <c>X-Forwarded-For</c>, <c>X-Forwarded-Proto</c> and <c>X-Forwarded-Host</c>
    /// headers, which most servers and frameworks read.
    /// </summary>
    XForwarded = 0,

    /// <summary>
    /// The standard <c>Forwarded</c> header (RFC 7239), one element per hop.
    /// </summary>
    Forwarded = 1,
}
