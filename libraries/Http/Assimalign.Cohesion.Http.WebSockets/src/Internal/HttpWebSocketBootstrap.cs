using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Http.Internal;

/// <summary>
/// The transport-specific half of a WebSocket opening handshake: how a protocol asks for a
/// WebSocket, which of the handshake's fields it uses, and how it hands over the connection. The
/// rest of the handshake (version, subprotocols, permessage-deflate, the framing) is shared, in
/// <see cref="HttpWebSocketFeature"/>.
/// </summary>
/// <remarks>
/// <para>
/// This is the seam that keeps the public surface protocol-neutral. HTTP/1.1 bootstraps a
/// WebSocket with an RFC 9110 §7.8 upgrade: a <c>GET</c> with <c>Sec-WebSocket-Key</c>, answered
/// with <c>101</c> and <c>Sec-WebSocket-Accept</c> (<see cref="Http1WebSocketBootstrap"/>).
/// HTTP/2 and HTTP/3 bootstrap it with extended CONNECT (RFC 8441, RFC 9220): a <c>CONNECT</c>
/// whose <c>:protocol</c> is <c>websocket</c>, answered with <c>200</c>, with no key or accept
/// value, and with no connection-specific fields in any response
/// (<see cref="ExtendedConnectWebSocketBootstrap"/>).
/// </para>
/// <para>
/// Both bootstraps hand over a duplex stream, and the framing that runs over it is the same on
/// every protocol; only the opening differs.
/// </para>
/// </remarks>
internal abstract class HttpWebSocketBootstrap
{
    /// <summary>
    /// Validates the request as an opening handshake on this protocol: its own fields, in the
    /// order that gives the client the most useful refusal, and the shared version rule
    /// (<see cref="HttpWebSocketHandshake.HasSupportedVersion"/>).
    /// </summary>
    /// <returns>
    /// <see cref="HttpWebSocketHandshakeStatus.Valid"/>,
    /// <see cref="HttpWebSocketHandshakeStatus.Invalid"/> or
    /// <see cref="HttpWebSocketHandshakeStatus.UnsupportedVersion"/>; never
    /// <see cref="HttpWebSocketHandshakeStatus.None"/>, since a bootstrap exists only for a request
    /// that asks for a WebSocket.
    /// </returns>
    public abstract HttpWebSocketHandshakeStatus Validate();

    /// <summary>
    /// Stages the protocol's own fields of an accepted handshake on the response, such as
    /// HTTP/1.1's <c>Sec-WebSocket-Accept</c>, and removes the handshake fields the protocol does
    /// not carry.
    /// </summary>
    /// <param name="responseHeaders">The exchange's response headers.</param>
    public abstract void ApplyAcceptFields(IHttpHeaderCollection responseHeaders);

    /// <summary>
    /// Stages the protocol's own fields of a <c>426 Upgrade Required</c> refusal, beyond the shared
    /// <c>Sec-WebSocket-Version</c>.
    /// </summary>
    /// <param name="responseHeaders">The exchange's response headers.</param>
    public abstract void ApplyUpgradeRequiredFields(IHttpHeaderCollection responseHeaders);

    /// <summary>
    /// Completes the protocol switch: sends the success response (<c>101</c> on HTTP/1.1,
    /// <c>200</c> on HTTP/2 and HTTP/3) with the staged headers and returns the duplex stream the
    /// WebSocket framing runs over.
    /// </summary>
    /// <param name="cancellationToken">A token that cancels the switch.</param>
    /// <returns>The stream; the WebSocket that wraps it owns it.</returns>
    /// <exception cref="InvalidOperationException">The exchange can no longer switch protocols.</exception>
    public abstract ValueTask<Stream> AcceptTransportAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Selects the bootstrap for the exchange's protocol.
    /// </summary>
    /// <param name="context">The exchange.</param>
    /// <returns>The bootstrap, or <see langword="null"/> when the request does not ask for a WebSocket.</returns>
    public static HttpWebSocketBootstrap? Select(IHttpContext context)
    {
        // HTTP/1.1 (RFC 6455 §4.1): an upgrade whose Upgrade header names websocket. The
        // protocol-upgrade interceptor surfaces it only on HTTP/1.1, and only when the connection
        // can be taken over. Its Protocol is the first token of the Upgrade header, the one a 101
        // switches to, so a request that prefers another protocol is not a WebSocket attempt.
        if (context.Upgrade is { Kind: HttpProtocolUpgradeKind.Upgrade } upgrade
            && string.Equals(upgrade.Protocol, HttpWebSocketHandshake.ProtocolToken, StringComparison.OrdinalIgnoreCase))
        {
            return new Http1WebSocketBootstrap(context.Request, upgrade);
        }

        // HTTP/2 and HTTP/3 (RFC 8441 §4, RFC 9220 §3): an extended CONNECT whose :protocol names
        // websocket. The extended CONNECT interceptor installs the feature only on an extended CONNECT
        // the transport validated, so its presence is the pseudo-header check; the method test first
        // keeps the feature lookup off every other request. An extended CONNECT for another protocol
        // is not a WebSocket attempt.
        if (context.Request.Method == HttpMethod.Connect
            && context.ExtendedConnect is { } extendedConnect
            && string.Equals(extendedConnect.Protocol, HttpWebSocketHandshake.ProtocolToken, StringComparison.OrdinalIgnoreCase))
        {
            return new ExtendedConnectWebSocketBootstrap(context.Request, extendedConnect);
        }

        return null;
    }
}
