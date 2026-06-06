namespace Assimalign.Cohesion.Http.Transports.Internal;

/// <summary>
/// Well-known <see cref="IHttpContext.Items"/> keys the HTTP/2 and HTTP/3
/// transports use to surface wire-level details a higher layer may model,
/// without the transport itself taking a dependency on that layer.
/// </summary>
internal static class TransportItemKeys
{
    /// <summary>
    /// The <c>:protocol</c> pseudo-header (RFC 8441 / RFC 9220 extended CONNECT).
    /// Surfaced verbatim whenever the pseudo-header is present so the
    /// <c>Assimalign.Cohesion.Http.ExtendedConnect</c> package can model extended
    /// CONNECT. The key is the pseudo-header name by convention; that package
    /// reads the same key.
    /// </summary>
    public const string Protocol = ":protocol";
}
