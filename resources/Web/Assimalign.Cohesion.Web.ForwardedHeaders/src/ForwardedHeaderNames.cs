using System;

namespace Assimalign.Cohesion.Web.ForwardedHeaders;

/// <summary>
/// Selects which proxy-forwarding headers the forwarded-headers middleware honors.
/// Header selection is part of the trust model: enable exactly the headers your
/// trusted proxy actually manages, and no others.
/// </summary>
/// <remarks>
/// <para>
/// A proxy that forwards a request appends its observation to the header family it is
/// configured for &#8212; either the RFC 7239 <c>Forwarded</c> header or the de-facto
/// <c>X-Forwarded-*</c> trio. A header the proxy does <em>not</em> manage arrives
/// exactly as the client sent it, so honoring it hands the nearest-hop position of the
/// chain to the client. Selecting a header here asserts "my trusted proxy maintains
/// this header"; the middleware then walks that header's entries rightmost-first
/// through the configured trust boundary.
/// </para>
/// <para>
/// When <see cref="Forwarded"/> is selected together with any <c>XForwarded*</c> flag
/// and a request carries both families, the RFC 7239 header takes precedence and the
/// legacy headers are ignored for that request &#8212; families are never mixed within one
/// exchange, because their entries cannot be correlated hop-for-hop.
/// </para>
/// </remarks>
[Flags]
public enum ForwardedHeaderNames
{
    /// <summary>
    /// No headers are honored. The middleware rejects this value at composition time —
    /// an explicit selection is required.
    /// </summary>
    None = 0,

    /// <summary>
    /// The RFC 7239 <c>Forwarded</c> header. Each element carries the hop's
    /// <c>for</c>/<c>proto</c>/<c>host</c> observations as one unit.
    /// </summary>
    Forwarded = 1 << 0,

    /// <summary>
    /// The de-facto <c>X-Forwarded-For</c> header (the client-address chain).
    /// </summary>
    XForwardedFor = 1 << 1,

    /// <summary>
    /// The de-facto <c>X-Forwarded-Proto</c> header (the original request scheme).
    /// </summary>
    XForwardedProto = 1 << 2,

    /// <summary>
    /// The de-facto <c>X-Forwarded-Host</c> header (the original <c>Host</c> value).
    /// </summary>
    XForwardedHost = 1 << 3,

    /// <summary>
    /// The whole de-facto trio: <see cref="XForwardedFor"/>, <see cref="XForwardedProto"/>,
    /// and <see cref="XForwardedHost"/>.
    /// </summary>
    XForwarded = XForwardedFor | XForwardedProto | XForwardedHost,

    /// <summary>
    /// Every supported forwarding header. Prefer selecting only what your proxy manages.
    /// </summary>
    All = Forwarded | XForwarded,
}
