# Assimalign.Cohesion.Http.ExtendedConnect — Overview

Gives applications the HTTP/2 and HTTP/3 *extended CONNECT* mechanism (RFC 8441 / RFC 9220) as a
feature on `IHttpContext`.

## Scope

- Detect that the current exchange is an extended CONNECT (a `CONNECT` request carrying the
  `:protocol` pseudo-header).
- Expose the requested `:protocol` value (for example `websocket`).
- Accept the exchange as a duplex tunnel: a `200` response head, then raw octets in both directions
  over the exchange's stream.

## Registration

The feature is installed by an exchange interceptor, so register it on the listener:

```csharp
options.Interceptors.Add(HttpExtendedConnect.CreateInterceptor());
```

The Web host (`Assimalign.Cohesion.Web.Hosting`) registers it by default. Without it, an extended
CONNECT reaches the application as an ordinary `CONNECT` and `context.ExtendedConnect` is `null`,
although the HTTP/2 and HTTP/3 transports still advertise extended CONNECT to clients. An ordinary
exchange pays nothing for the registration beyond a version check.

## Usage

```csharp
using Assimalign.Cohesion.Http;

if (context.ExtendedConnect is { Protocol: "websocket" } extendedConnect)
{
    await using Stream tunnel = await extendedConnect.AcceptAsync(context.RequestCancelled);

    // Reads return the client's DATA; writes go out as DATA at once. Disposing ends the server's
    // side (END_STREAM / FIN). For WebSocket, run the BCL framing over the tunnel:
    using WebSocket socket = WebSocket.CreateFromStream(tunnel, new WebSocketCreationOptions { IsServer = true });
    // ...
}
```

Set any response headers (for example `sec-websocket-protocol`) before accepting; they travel on the
`200`.

For a WebSocket, use `context.WebSockets` (`Assimalign.Cohesion.Http.WebSockets`) instead: it
validates the RFC 8441 / RFC 9220 handshake (`sec-websocket-version: 13`), negotiates the
subprotocol and permessage-deflate, and accepts through this feature, with the same calls as on
HTTP/1.1.

## Dependencies

- `Assimalign.Cohesion.Http` — the protocol core: the interceptor seam, the validated `:protocol` on
  the request context, and the exchange control's `AcceptTunnelAsync`.

The HTTP/2 and HTTP/3 transports (`Assimalign.Cohesion.Http.Connections`) validate extended CONNECT
and implement the tunnel accept on their exchange controls; this package's interceptor wraps it into
the feature. Neither references the other.

## Non-goals

No WebSocket framing (the BCL provides it; `Http.WebSockets` owns the handshake), no classic CONNECT
tunneling, and no client-side initiation — see [DESIGN.md](./DESIGN.md).
