# Assimalign.Cohesion.Http.ProtocolUpgrade — Overview

Models the HTTP/1.1 connection transitions — RFC 9110 §7.8 protocol **upgrade**
(`101 Switching Protocols`) and RFC 9110 §9.3.6 **CONNECT** tunnels (`200 OK`) — as an opt-in
capability on `IHttpContext`, wired entirely through the server transport's interceptor seams.

## Scope

- Detect that an HTTP/1.1 exchange is an upgrade or CONNECT (surfaced via `context.Upgrade`).
- Accept the transition: write the `101` / `200` response and surrender the raw duplex
  transport stream for the negotiated protocol or tunnel.

## Usage

Register the single interceptor on the listener options (one registration — detection rides the
request seam, acceptance rides the response seam):

```csharp
using Assimalign.Cohesion.Http;

HttpConnectionListenerOptions options = new();
options.Interceptors.Add(HttpProtocolUpgrade.CreateInterceptor());
```

Then, in a handler:

```csharp
if (context.Upgrade is { Kind: HttpProtocolUpgradeKind.Upgrade, Protocol: "websocket" } upgrade)
{
    Stream tunnel = await upgrade.AcceptAsync(context.RequestCancelled);
    // ... drive the negotiated protocol over `tunnel` (e.g. the WebSocket framing layer).
}
// context.Upgrade is null for ordinary exchanges, on HTTP/2 / HTTP/3, and when the
// interceptors are not registered.
```

## Dependencies

- `Assimalign.Cohesion.Http` — the protocol core (`IHttpContext`, the interceptor seams, and the
  generic `IHttpExchangeControl` per-exchange control surface).
- `Assimalign.Cohesion.Http.Cookies` — to drain response cookies onto the transition response.

The server transport (`Assimalign.Cohesion.Http.Connections`) surfaces its exchange control
(`HttpResponseInterceptorContext.Control`) on the response-interceptor seam; this package's
interceptors consume its takeover capability — `CanTakeOver` / `TakeOver()`, which only the
HTTP/1.1 exchange control offers. There is **no** compile-time dependency between the transport
and this package (the layering constraint of #751).

## Non-goals

This package does not provide a WebSocket (RFC 6455) framing surface — it surrenders the stream
after the handshake. See [DESIGN.md](./DESIGN.md) for the interceptor-based design and its
rationale.
