# Assimalign.Cohesion.Http.WebSockets — Overview

Server WebSockets for the Cohesion HTTP family: the RFC 6455 opening handshake, subprotocol
selection and permessage-deflate negotiation (RFC 7692), ending in a BCL
`System.Net.WebSockets.WebSocket`. The framing is the BCL's; this package owns the handshake.

## Scope

- Detect a WebSocket opening handshake and validate it (`context.WebSockets.HandshakeStatus`,
  `IsWebSocketRequest`).
- Refuse a handshake the server cannot accept, as RFC 6455 §4.2 prescribes: `400` when it is
  malformed, `426` with `Sec-WebSocket-Version: 13` for another version (`RejectHandshake`).
- Accept it: select a subprotocol the client offered, negotiate permessage-deflate when enabled,
  answer `101` with `Sec-WebSocket-Accept`, and return the server end of the socket
  (`AcceptWebSocketAsync`).

HTTP/1.1 only in this version: a WebSocket over HTTP/2 or HTTP/3 extended CONNECT (RFC 8441,
RFC 9220) arrives in phase 2 without changing this surface (see [DESIGN.md](./DESIGN.md)).

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

The endpoint keeps running for as long as the socket is open; the server ends the connection when
the exchange completes. To select a subprotocol, pick one of `webSockets.RequestedProtocols` (the
client's offer, in its order of preference) and pass it as `HttpWebSocketAcceptOptions.SubProtocol`.

On a bare HTTP/1.1 listener, register the protocol-upgrade interceptor yourself, and check `Origin`
before accepting a socket that serves browsers:

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

- `Assimalign.Cohesion.Http` — the protocol core (`IHttpContext`, the feature collection, headers).
- `Assimalign.Cohesion.Http.ProtocolUpgrade` — the HTTP/1.1 upgrade and raw-stream takeover the
  handshake rides.

The framing is `System.Net.WebSockets` from the shared framework; there is no package dependency.

## Non-goals

No frame codec (the BCL's is used), no origin policy (that is `Web.WebSockets`), no client, and no
HTTP/2 or HTTP/3 in this version. See [DESIGN.md](./DESIGN.md).
