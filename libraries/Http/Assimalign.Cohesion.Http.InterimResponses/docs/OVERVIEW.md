# Assimalign.Cohesion.Http.InterimResponses — Overview

Interim (`1xx`) responses as an opt-in feature package that plugs into the
transport via the response-interceptor seam.

## Purpose

Let a handler emit one or more interim responses ahead of the final response
(RFC 9110 §15.2) — most usefully `100 Continue` (§10.1.1) on demand and
`103 Early Hints` (RFC 8297) with `Link` fields — over any transport that exposes
the `IHttpResponseInterceptor` seam, without the transport (or the protocol core)
depending on this package.

## Scope

- `IHttpInterimResponseFeature` — the typed interim-response API
  (`IsInterimResponseSupported`, `SendInterimResponseAsync`).
- `HttpInterimResponses.CreateInterceptor()` — the `IHttpResponseInterceptor` a
  host registers to make interim responses available on every exchange.
- `context.InterimResponse` / `context.SendEarlyHintsAsync(...)` /
  `context.SendContinueAsync()` — ergonomic accessors on `IHttpContext`.

## Dependencies

- `Assimalign.Cohesion.Http` (the response-interceptor seam, the
  `IHttpExchangeControl` exchange control, the feature collection).
- **Not** `Assimalign.Cohesion.Http.Connections` — interim responses are
  transport-agnostic; the transport owns the wire emission behind the control.

## Relationship to `Expect: 100-continue`

The HTTP/1.1 transport emits `100 Continue` **automatically** for a request that
carries `Expect: 100-continue` with a framed body — a wire-level interop concern
(a large-upload client withholds its body until it is solicited). That behavior
lives in the transport and works whether or not this package is registered. This
package covers the **application-initiated** interim responses: `103 Early Hints`
and an explicit, handler-driven `100 Continue`.

## Usage

```csharp
// Host / composition root — opt into interim responses:
options.ResponseInterceptors.Add(HttpInterimResponses.CreateInterceptor());

// Handler — send 103 Early Hints before doing the slow work:
await context.SendEarlyHintsAsync(["</style.css>; rel=preload; as=style"]);
// ... compute the real response ...
context.Response.StatusCode = HttpStatusCode.Ok;
```
