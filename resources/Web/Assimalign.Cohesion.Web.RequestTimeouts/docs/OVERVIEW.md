# Assimalign.Cohesion.Web.RequestTimeouts — Overview

Request-timeout policies for the Cohesion Web pipeline. Cohesion's protocol core already ships
the right primitive — the per-exchange, transport-aware cancel (`IHttpContext.RequestCancelled` +
`Cancel`/`CancelAsync`, from #703) — but nothing armed it: a slow handler ran forever. This
package is the policy layer that arms it.

## What it does

- **Global default policy** — configured at builder time on `UseRequestTimeouts`, applied to
  every request the middleware sees.
- **Per-endpoint policies** — a sealed `RequestTimeoutMetadata` carrier attached to a route's
  endpoint-metadata bag (the #150 seam) overrides the default, resolved last-wins. An endpoint
  can also opt out entirely via `RequestTimeoutMetadata.Disabled`.
- **Expiry → cancellation** — the middleware hands downstream a context whose
  `RequestCancelled` is a linked token that trips on expiry, so handlers, the router's
  per-dispatch token, and anything else awaiting the request token unwind cooperatively.
- **Expiry → response** — when the response has not started, the timed-out request is answered
  imperatively with the policy's status (504 `Gateway Timeout` by default), optionally an
  RFC 9457 `application/problem+json` payload (via `Web.ProblemDetails`), or a fully custom
  `WriteResponse` handler. When the response *has* started (streamed head already committed),
  the exchange is aborted cleanly at the protocol layer instead (`IHttpContext.CancelAsync`).
- **Per-exchange control** — an `IHttpRequestTimeoutFeature` on the feature collection lets a
  handler `Disable()` or re-arm (`SetTimeout`) its own exchange, mirroring ASP.NET's
  `DisableRequestTimeout`.
- **Debugger suspension** — enforcement is suspended while a debugger is attached (mirrors
  ASP.NET; opt out via `RequestTimeoutOptions.SuspendWhenDebuggerAttached`).
- **TimeProvider-based** — timers measure against `RequestTimeoutOptions.TimeProvider`
  (`TimeProvider.System` by default), so expiry is deterministic under a test clock.

## Usage

```csharp
// Global default: every request gets 30 seconds.
application.UseRequestTimeouts(TimeSpan.FromSeconds(30));

// Or configured: 504 with a problem+json payload.
application.UseRequestTimeouts(options => options.DefaultPolicy = new RequestTimeoutPolicy
{
    Timeout = TimeSpan.FromSeconds(30),
    WriteProblemDetails = true,
});

// Register BEFORE UseRouting so routed endpoints are governed and per-endpoint
// metadata is observed.
IRouterBuilder routes = application.UseRouting();

// Per-endpoint override / disable via the endpoint metadata bag.
routes.Map(new Route(HttpMethod.Get, "/report", handler,
    new RouterRouteMetadataCollection(new RequestTimeoutMetadata(TimeSpan.FromMinutes(2)))));
routes.Map(new Route(HttpMethod.Get, "/stream", streamHandler,
    new RouterRouteMetadataCollection(RequestTimeoutMetadata.Disabled)));
```

## Public surface

| Type | Role |
| --- | --- |
| `RequestTimeoutPolicy` | The policy value: `Timeout` (null = disabled), `StatusCode` (default 504), `WriteProblemDetails`, `WriteResponse` |
| `RequestTimeoutMetadata` | Sealed endpoint-metadata carrier for a policy; `Disabled` opts an endpoint out |
| `RequestTimeoutOptions` | Middleware options: `DefaultPolicy`, `TimeProvider`, `SuspendWhenDebuggerAttached` |
| `IHttpRequestTimeoutFeature` | Per-exchange feature: `Token`, `Disable()`, `SetTimeout(TimeSpan)` |
| `WebApplicationExtensions` | `UseRequestTimeouts(...)` pipeline verbs |

## Dependencies

`Assimalign.Cohesion.Web` (pipeline seams) · `Assimalign.Cohesion.Web.Routing` (route-match
feature + endpoint metadata) · `Assimalign.Cohesion.Web.ProblemDetails` (timeout payload) ·
`Assimalign.Cohesion.Http.Streaming` (response-started probe). Per the Web-area dependency rule
it references no hosting module, holds no DI/configuration/logging state, and is delivered to
applications through the `App.Web` shared framework.

Design rationale — including why expiry does **not** trip the transport cancel while the
response is writable — lives in [DESIGN.md](DESIGN.md).
