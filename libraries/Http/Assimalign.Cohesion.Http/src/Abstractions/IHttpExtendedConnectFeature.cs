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
/// websocket</c>) over a single HTTP/2 or HTTP/3 stream. The transport installs
/// this feature only when a request is a valid extended CONNECT; its presence is
/// the explicit, opt-in signal that the exchange is an extension transition
/// rather than a normal request. Ordinary requests do not carry the feature, so
/// <see cref="HttpFeatureCollectionExtensions.Get{TFeature}"/> returns
/// <see langword="null"/> for them.
/// </para>
/// <para>
/// This contract intentionally exposes only the requested protocol — it does not
/// surrender a tunnel stream. Cohesion recognizes and models extended CONNECT
/// (so an application can detect it and respond deterministically) but does not
/// ship a WebSocket framing surface; see the transports <c>DESIGN.md</c> for the
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
