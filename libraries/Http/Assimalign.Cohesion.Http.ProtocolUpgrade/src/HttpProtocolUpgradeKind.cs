namespace Assimalign.Cohesion.Http;

/// <summary>
/// Identifies the kind of HTTP/1.1 connection transition signalled by a request.
/// </summary>
/// <remarks>
/// <para>
/// HTTP/1.1 defines two ways for a single connection to leave the request/response
/// loop and become something else:
/// </para>
/// <list type="bullet">
///   <item><description><see cref="Upgrade"/> — the request carries
///   <c>Connection: upgrade</c> together with an <c>Upgrade</c> header listing a
///   protocol token (RFC 9110 §7.8). When the server accepts, it responds with
///   <c>101 Switching Protocols</c> and the connection becomes the negotiated
///   protocol.</description></item>
///   <item><description><see cref="Connect"/> — the request uses the
///   <c>CONNECT</c> method with an authority-form request-target (RFC 9112 §3.2.3
///   and RFC 9110 §9.3.6). When the server accepts, it responds with a 2xx and the
///   connection becomes a tunnel carrying opaque octets between the client and the
///   target authority.</description></item>
/// </list>
/// <para>
/// <see cref="None"/> means the request is a normal request/response exchange and
/// no transition is available.
/// </para>
/// </remarks>
public enum HttpProtocolUpgradeKind
{
    /// <summary>
    /// No connection transition is available for the current exchange.
    /// </summary>
    None = 0,

    /// <summary>
    /// The request asked for a protocol upgrade through <c>Connection: upgrade</c>
    /// and the <c>Upgrade</c> header. Accepting writes <c>101 Switching Protocols</c>.
    /// </summary>
    Upgrade = 1,

    /// <summary>
    /// The request used the <c>CONNECT</c> method to open a tunnel to an authority.
    /// Accepting writes a 2xx response and the connection becomes opaque.
    /// </summary>
    Connect = 2,
}
