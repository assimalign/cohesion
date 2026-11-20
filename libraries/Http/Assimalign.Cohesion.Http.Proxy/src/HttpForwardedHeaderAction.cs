namespace Assimalign.Cohesion.Http;

/// <summary>
/// Selects what a forwarder does with the headers that tell the destination who the client is
/// (<c>X-Forwarded-For</c>, <c>X-Forwarded-Proto</c>, <c>X-Forwarded-Host</c> and <c>Forwarded</c>).
/// </summary>
public enum HttpForwardedHeaderAction
{
    /// <summary>
    /// Copies the client's headers unchanged and adds none.
    /// </summary>
    None = 0,

    /// <summary>
    /// Removes every such header the client sent, of both families, and adds one hop in the
    /// configured format describing the client as this application resolved it: the effective remote
    /// address, scheme and host, which a trusted forwarded-headers policy may already have taken from
    /// an earlier proxy. Use it where this proxy is the edge that clients reach.
    /// </summary>
    Set = 1,

    /// <summary>
    /// Keeps the client's headers of the configured family and appends one hop describing the
    /// connection this proxy received: the peer's address, the request's scheme and its <c>Host</c>.
    /// Headers of the other family are copied unchanged. Use it behind another proxy whose chain the
    /// destination reads.
    /// </summary>
    Append = 2,
}
