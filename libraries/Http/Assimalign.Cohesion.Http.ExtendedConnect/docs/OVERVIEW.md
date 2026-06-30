# Assimalign.Cohesion.Http.ExtendedConnect — Overview

Models the HTTP/2 and HTTP/3 *extended CONNECT* mechanism (RFC 8441 / RFC 9220)
as an opt-in feature on `IHttpContext`.

## Scope

- Detect that the current exchange is an extended CONNECT (a `CONNECT` request
  carrying the `:protocol` pseudo-header).
- Expose the requested `:protocol` value (for example `websocket`).

## Usage

```csharp
using Assimalign.Cohesion.Http;

if (context.IsExtendedConnect)
{
    string protocol = context.ExtendedConnect!.Protocol; // e.g. "websocket"
    // ... application decides how to respond / bootstrap the inner protocol.
}
```

## Dependencies

- `Assimalign.Cohesion.Http` — the protocol core (`IHttpContext`, `IHttpFeature`).

The HTTP/2 and HTTP/3 transports (`Assimalign.Cohesion.Http.Connections`) surface
the `:protocol` value through `IHttpContext.Items`; this package reads it. There
is no compile-time dependency between the transport and this package.

## Non-goals

This package does not provide a WebSocket framing surface and does not surrender
a tunnel stream — see [DESIGN.md](./DESIGN.md) for the scope decision.
