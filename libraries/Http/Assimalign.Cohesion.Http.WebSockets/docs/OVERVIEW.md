# Assimalign.Cohesion.Http.WebSockets — Overview

Server WebSockets for the Cohesion HTTP family: the opening handshake on HTTP/1.1 (RFC 6455),
HTTP/2 (RFC 8441) and HTTP/3 (RFC 9220), subprotocol selection and permessage-deflate negotiation
(RFC 7692), ending in a BCL `System.Net.WebSockets.WebSocket`. The framing is the BCL's; this
package owns the handshake.

## Scope

- Detect a WebSocket opening handshake and validate it (`context.WebSockets.HandshakeStatus`,
  `IsWebSocketRequest`): an HTTP/1.1 upgrade to `websocket`, or an HTTP/2 or HTTP/3 extended
  CONNECT whose `:protocol` is `websocket`.
- Refuse a handshake the server cannot accept, as RFC 6455 §4.2 prescribes: `400` when it is
  malformed, `426` with `Sec-WebSocket-Version: 13` for another version (`RejectHandshake`).
- Accept it: select a subprotocol the client offered, negotiate permessage-deflate when enabled,
  answer (`101` with `Sec-WebSocket-Accept` on HTTP/1.1, `200` on HTTP/2 and HTTP/3), and return
  the server end of the socket (`AcceptWebSocketAsync`).

The surface is the same on every protocol; an endpoint does not branch on the version (see
[DESIGN.md](./DESIGN.md)).

## Usage

On a Web application, map the endpoint with `MapWebSocket` (`Assimalign.Cohesion.Web.WebSockets`).
It routes both handshake methods, `GET` on HTTP/1.1 and `CONNECT` on HTTP/2 and HTTP/3, refuses a
request that is not a handshake, applies the origin policy, and accepts; the handler drives the
socket. The host registers the protocol-upgrade interceptor.

```csharp
using System.Net.WebSockets;
using Assimalign.Cohesion.Http;
using Assimalign.Cohesion.Web.WebSockets;

app.MapWebSocket("/chat", async (IHttpContext context, WebSocket socket) =>
{
    byte[] buffer = new byte[4096];
    WebSocketReceiveResult result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), context.RequestCancelled);
    while (result.MessageType != WebSocketMessageType.Close)
    {
        await socket.SendAsync(new ArraySegment<byte>(buffer, 0, result.Count), result.MessageType, result.EndOfMessage, context.RequestCancelled);
        result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), context.RequestCancelled);
    }

    await socket.CloseAsync(result.CloseStatus ?? WebSocketCloseStatus.NormalClosure, result.CloseStatusDescription, CancellationToken.None);
});
```

Code that serves the exchange itself uses this package's surface directly:

```csharp
IHttpWebSocketFeature webSockets = context.WebSockets;
if (!webSockets.IsWebSocketRequest)
{
    context.Response.StatusCode = HttpStatusCode.BadRequest;
    return;
}

using WebSocket socket = await webSockets.AcceptWebSocketAsync(cancellationToken: context.RequestCancelled);
```

It must be reached by both handshake methods, which a `MapGet` route is not: over HTTP/2 and HTTP/3
the handshake is a `CONNECT`. The exchange keeps running for as long as the socket is open; the
server ends the connection (HTTP/1.1) or the stream (HTTP/2, HTTP/3) when the exchange completes. To
select a subprotocol, pick one of `webSockets.RequestedProtocols` (the client's offer, in its order
of preference) and pass it as `HttpWebSocketAcceptOptions.SubProtocol`.

On a bare listener, register the interceptors yourself, and check `Origin` before accepting a socket
that serves browsers: the protocol-upgrade interceptor for HTTP/1.1, and the extended CONNECT
interceptor for HTTP/2 and HTTP/3. The Web host registers both by default; a host that clears its
listener's interceptors loses WebSockets on the protocols whose interceptor it removed.

```csharp
HttpConnectionListener listener = HttpConnectionListener.Create(options =>
{
    options.UseHttp1(tcpListener);
    options.Interceptors.Add(HttpProtocolUpgrade.CreateInterceptor());
    options.Interceptors.Add(HttpExtendedConnect.CreateInterceptor());
});
```

Compression is off unless the accept enables it
(`new HttpWebSocketAcceptOptions { DangerousEnableCompression = true }`): compressing secrets
alongside attacker-controlled data leaks them through the compressed size.

## Dependencies

- `Assimalign.Cohesion.Http` — the protocol core (`IHttpContext`, the feature collection, and
  headers).
- `Assimalign.Cohesion.Http.ProtocolUpgrade` — the HTTP/1.1 upgrade and raw-stream takeover the
  handshake rides.
- `Assimalign.Cohesion.Http.ExtendedConnect` — `IHttpExtendedConnectFeature`, the HTTP/2 and HTTP/3
  extended CONNECT tunnel the handshake rides.

The framing is `System.Net.WebSockets` from the shared framework; there is no package dependency.

## Non-goals

No frame codec (the BCL's is used), no origin policy (that is `Web.WebSockets`), and no client. See
[DESIGN.md](./DESIGN.md).
