using System;
using System.Collections.Generic;
using System.Net.WebSockets;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Http;

/// <summary>
/// The WebSocket capability of the current exchange: whether the request is a WebSocket opening
/// handshake, the subprotocols it offers, and the call that accepts it. Read it through
/// <c>context.WebSockets</c> (<see cref="HttpContextWebSocketExtensions"/>).
/// </summary>
/// <remarks>
/// <para>
/// Accepting completes the opening handshake and returns a BCL
/// <see cref="WebSocket"/>: the framing, masking, control-frame, close-code, UTF-8 and
/// permessage-deflate rules of RFC 6455 and RFC 7692 come from
/// <see cref="WebSocket.CreateFromStream(System.IO.Stream, WebSocketCreationOptions)"/>. This
/// package owns only the handshake and its negotiation.
/// </para>
/// <code>
/// IHttpWebSocketFeature webSockets = context.WebSockets;
/// if (webSockets.IsWebSocketRequest)
/// {
///     using WebSocket socket = await webSockets.AcceptWebSocketAsync(cancellationToken: context.RequestCancelled);
///     // ...receive and send messages until the close handshake...
/// }
/// </code>
/// <para>
/// On HTTP/1.1 the handshake is an RFC 9110 §7.8 upgrade, so the protocol-upgrade interceptor
/// (<c>HttpProtocolUpgrade.CreateInterceptor()</c>) must be registered on the listener; the Web host
/// registers it by default. An HTTP/2 or HTTP/3 request is not a WebSocket opening handshake in this
/// version of the package: <see cref="HandshakeStatus"/> reads
/// <see cref="HttpWebSocketHandshakeStatus.None"/>.
/// </para>
/// <para>
/// The exchange must keep running for as long as the socket is open: the server ends the
/// connection when the exchange completes. A policy layer can replace the feature with a decorator
/// that applies defaults to every accept, as the Web pipeline's <c>UseWebSockets</c> does
/// (<c>Assimalign.Cohesion.Web.WebSockets</c>). Without one, nothing checks the handshake's
/// <c>Origin</c>, so an application that serves browsers checks it before accepting.
/// </para>
/// </remarks>
public interface IHttpWebSocketFeature : IHttpFeature
{
    /// <summary>
    /// Gets the result of validating the request as a WebSocket opening handshake.
    /// </summary>
    HttpWebSocketHandshakeStatus HandshakeStatus { get; }

    /// <summary>
    /// Gets whether the request is a valid WebSocket opening handshake that
    /// <see cref="AcceptWebSocketAsync"/> can accept (<see cref="HandshakeStatus"/> is
    /// <see cref="HttpWebSocketHandshakeStatus.Valid"/>).
    /// </summary>
    bool IsWebSocketRequest { get; }

    /// <summary>
    /// Gets the subprotocols the client offered in <c>Sec-WebSocket-Protocol</c>, in its order of
    /// preference. Empty when it offered none.
    /// </summary>
    IReadOnlyList<string> RequestedProtocols { get; }

    /// <summary>
    /// Accepts the opening handshake and returns the server end of the WebSocket.
    /// </summary>
    /// <remarks>
    /// The response carries the response headers the application set beforehand, plus
    /// <c>Sec-WebSocket-Accept</c>, the selected <c>Sec-WebSocket-Protocol</c> and, when compression
    /// was negotiated, <c>Sec-WebSocket-Extensions</c>. The caller owns the returned socket and
    /// disposes it.
    /// </remarks>
    /// <param name="options">
    /// The subprotocol, keep-alive and compression settings for this socket, or
    /// <see langword="null"/> for the defaults.
    /// </param>
    /// <param name="cancellationToken">A token that cancels the accept.</param>
    /// <returns>The accepted WebSocket, in the <see cref="WebSocketState.Open"/> state.</returns>
    /// <exception cref="ArgumentException">
    /// <paramref name="options"/> selects a subprotocol the client did not offer.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// The request is not a valid opening handshake (<see cref="IsWebSocketRequest"/> is
    /// <see langword="false"/>), the handshake was already accepted, or the exchange can no longer
    /// switch protocols because its response has started.
    /// </exception>
    ValueTask<WebSocket> AcceptWebSocketAsync(
        HttpWebSocketAcceptOptions? options = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Refuses an opening handshake the server cannot accept, as RFC 6455 §4.2 prescribes:
    /// <c>400 Bad Request</c> for a malformed handshake, and <c>426 Upgrade Required</c> with
    /// <c>Sec-WebSocket-Version: 13</c> for a version the server does not speak.
    /// </summary>
    /// <remarks>
    /// The method stages the status and headers on the response; the response is sent when the
    /// exchange completes. A valid handshake is refused simply by not accepting it.
    /// </remarks>
    /// <exception cref="InvalidOperationException">
    /// <see cref="HandshakeStatus"/> is neither <see cref="HttpWebSocketHandshakeStatus.Invalid"/>
    /// nor <see cref="HttpWebSocketHandshakeStatus.UnsupportedVersion"/>.
    /// </exception>
    void RejectHandshake();
}
