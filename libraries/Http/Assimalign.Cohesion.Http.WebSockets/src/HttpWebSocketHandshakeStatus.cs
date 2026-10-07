namespace Assimalign.Cohesion.Http;

/// <summary>
/// The result of validating a request as a WebSocket opening handshake (RFC 6455 §4.2.1).
/// </summary>
/// <remarks>
/// A request is a handshake <em>attempt</em> when it asks to switch to the WebSocket protocol: on
/// HTTP/1.1, an upgrade (<c>Connection: Upgrade</c>) whose <c>Upgrade</c> header names
/// <c>websocket</c>; on HTTP/2 and HTTP/3, an extended CONNECT whose <c>:protocol</c> is
/// <c>websocket</c> (RFC 8441, RFC 9220). An attempt that breaks the handshake's rules is refused
/// rather than served as an ordinary request (<see cref="IHttpWebSocketFeature.RejectHandshake"/>).
/// </remarks>
public enum HttpWebSocketHandshakeStatus
{
    /// <summary>
    /// The request does not ask for a WebSocket. It is an ordinary request.
    /// </summary>
    None = 0,

    /// <summary>
    /// The request is a valid opening handshake, which
    /// <see cref="IHttpWebSocketFeature.AcceptWebSocketAsync"/> can accept.
    /// </summary>
    Valid = 1,

    /// <summary>
    /// The request asks for a WebSocket, but its handshake is malformed: its
    /// <c>Sec-WebSocket-Protocol</c> is not a list of tokens, or, on HTTP/1.1, it is not a
    /// <c>GET</c>, carries content, or its <c>Sec-WebSocket-Key</c> is missing or is not the base64
    /// encoding of 16 bytes (an extended CONNECT carries no key, RFC 8441 §5). The server refuses it
    /// with <c>400 Bad Request</c>.
    /// </summary>
    Invalid = 2,

    /// <summary>
    /// The request asks for a WebSocket version other than 13, or names none. The server refuses it
    /// with <c>426 Upgrade Required</c> and <c>Sec-WebSocket-Version: 13</c> (RFC 6455 §4.4), so
    /// the client can retry with the version the server speaks.
    /// </summary>
    UnsupportedVersion = 3,
}
