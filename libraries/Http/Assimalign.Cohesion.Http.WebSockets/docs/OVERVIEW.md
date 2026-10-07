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

On a Web application the host registers the protocol-upgrade interceptor, and `UseWebSockets`
(`Assimalign.Cohesion.Web.WebSockets`) adds the origin policy. An endpoint then accepts:

```csharp
using System.Net.WebSockets;
using Assimalign.Cohesion.Http;

app.MapGet("/chat", async (IHttpContext context) =>
{
    IHttpWebSocketFeature webSockets = context.WebSockets;
    if (!webSockets.IsWebSocketRequest)
    {
        context.Response.StatusCode = HttpStatusCode.BadRequest;
        return;
    }

    using WebSocket socket = await webSockets.AcceptWebSocketAsync(cancellationToken: context.RequestCancelled);

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

The endpoint keeps running for as long as the socket is open; the server ends the connection
(HTTP/1.1) or the stream (HTTP/2, HTTP/3) when the exchange completes. To select a subprotocol, pick
one of `webSockets.RequestedProtocols` (the client's offer, in its order of preference) and pass it
as `HttpWebSocketAcceptOptions.SubProtocol`. Over HTTP/2 and HTTP/3 the handshake's method is
`CONNECT`, so a route mapped for `GET` only does not match it; map the endpoint for `CONNECT` too.

On a bare HTTP/1.1 listener, register the protocol-upgrade interceptor yourself, and check `Origin`
before accepting a socket that serves browsers. HTTP/2 and HTTP/3 listeners need nothing registered:
the transport surfaces extended CONNECT itself.

```csharp
HttpConnectionListener listener = HttpConnectionListener.Create(options =>
{
    options.UseHttp1(tcpListener);
    options.Interceptors.Add(HttpProtocolUpgrade.CreateInterceptor());
});
```

Compression is off unless the accept enables it
(`new HttpWebSocketAcceptOptions { DangerousEnableCompression = true }`): compressing secrets
alongside attacker-controlled data leaks them through the compressed size.

## Dependencies

- `Assimalign.Cohesion.Http` — the protocol core (`IHttpContext`, the feature collection, headers,
  and `IHttpExtendedConnectFeature`, the HTTP/2 and HTTP/3 tunnel the handshake rides).
- `Assimalign.Cohesion.Http.ProtocolUpgrade` — the HTTP/1.1 upgrade and raw-stream takeover the
  handshake rides.

The framing is `System.Net.WebSockets` from the shared framework; there is no package dependency.

## Non-goals

No frame codec (the BCL's is used), no origin policy (that is `Web.WebSockets`), and no client. See
[DESIGN.md](./DESIGN.md).
