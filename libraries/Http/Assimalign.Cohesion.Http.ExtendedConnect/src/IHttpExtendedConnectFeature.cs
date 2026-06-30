namespace Assimalign.Cohesion.Http;

/// <summary>
/// Marks the current exchange as an HTTP/2 or HTTP/3 <em>extended CONNECT</em>
/// request (RFC 8441 / RFC 9220) and exposes the requested protocol.
/// </summary>
/// <remarks>
/// <para>
/// An extended CONNECT request is a <c>CONNECT</c> request that additionally
/// carries the <c>:protocol</c> pseudo-header — the mechanism a client uses to
/// bootstrap another protocol (most commonly WebSocket, <c>:protocol =
/// websocket</c>) over a single HTTP/2 or HTTP/3 stream. The feature is surfaced
/// by the <see cref="HttpExtendedConnectExtensions"/> members when a request is
/// an extended CONNECT; ordinary requests do not carry it.
/// </para>
/// <para>
/// This contract intentionally exposes only the requested protocol — it does not
/// surrender a tunnel stream. This package recognizes and models extended
/// CONNECT (so an application can detect it and respond deterministically) but
/// does not ship a WebSocket framing surface; see <c>docs/DESIGN.md</c> for the
/// scope decision.
/// </para>
/// </remarks>
public interface IHttpExtendedConnectFeature : IHttpFeature
{
    /// <summary>
    /// Gets the value of the <c>:protocol</c> pseudo-header the client requested
    /// (for example <c>websocket</c>). Never <see langword="null"/> or empty when
    /// the feature is present.
    /// </summary>
    string Protocol { get; }
}
