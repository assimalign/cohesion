# Assimalign.Cohesion.Web.RequestTimeouts — Overview

Request-timeout policies for the Cohesion Web pipeline. Cohesion's protocol core already ships
the right primitive — the per-exchange, transport-aware cancel (`IHttpContext.RequestCancelled` +
`Cancel`/`CancelAsync`, from #703) — but nothing armed it: a slow handler ran forever. This
package is the policy layer that arms it.

## What it does

- **Global default policy** — configured at builder time on `UseRequestTimeouts`, applied to
  every request the middleware sees.
- **Per-endpoint policies** — a sealed `RequestTimeoutMetadata` carrier attached to a route's
  endpoint-metadata bag (the #150 seam) overrides the default, resolved last-wins from the
  endpoint `UseRouting` publishes. An endpoint can also opt out entirely via
  `RequestTimeoutMetadata.Disabled`.
- **Expiry → cancellation** — the middleware hands downstream a context whose
  `RequestCancelled` is a linked token that trips on expiry, so handlers, the endpoint the
  pipeline terminal runs, and anything else awaiting the request token unwind cooperatively.
- **Expiry → response** — when the response has not started, the timed-out request is answered
  imperatively with the policy's status (504 `Gateway Timeout` by default), optionally an
  RFC 9457 `application/problem+json` payload (via `Web.ProblemDetails`), or a fully custom
  `WriteResponse` handler. When the response *has* started (streamed head already committed),
  the exchange is aborted cleanly at the protocol layer instead (`IHttpContext.CancelAsync`).
- **Per-exchange control** — an `IRequestTimeoutFeature` on the feature collection lets a
  handler `Disable()` or re-arm (`SetTimeout`) its own exchange, mirroring ASP.NET's
  `DisableRequestTimeout`.
- **Debugger suspension** — enforcement is suspended while a debugger is attached (mirrors
  ASP.NET; opt out via `RequestTimeoutOptions.SuspendWhenDebuggerAttached`).
- **TimeProvider-based** — timers measure against `RequestTimeoutOptions.TimeProvider`
  (`TimeProvider.System` by default), so expiry is deterministic under a test clock.

## Usage

```csharp
IRouterBuilder routes = application.UseRouting();

// Register AFTER UseRouting, so the middleware reads the published endpoint's policy.
// Global default: every request gets 30 seconds.
application.UseRequestTimeouts(TimeSpan.FromSeconds(30));

// Or configured: 504 with a problem+json payload.
application.UseRequestTimeouts(options => options.DefaultPolicy = new RequestTimeoutPolicy
{
    Timeout = TimeSpan.FromSeconds(30),
    WriteProblemDetails = true,
});

// Per-endpoint override / disable where the endpoint is mapped (a route group takes the same verbs).
application.MapGet("/report", handler).WithRequestTimeout(TimeSpan.FromMinutes(2));
application.MapGet("/stream", streamHandler).DisableRequestTimeout();

// The verbs append RequestTimeoutMetadata; attaching it through the route's metadata is equivalent.
routes.Map(new Route(HttpMethod.Get, "/export", exportHandler,
    new RouterRouteMetadataCollection(new RequestTimeoutMetadata(TimeSpan.FromMinutes(5)))));
```

Registered ahead of `UseRouting`, the global default still governs every request, but no endpoint
is known when the middleware runs: an endpoint whose metadata carries a timeout fails with
`InvalidOperationException` when it is dispatched, instead of running unbounded.

## Public surface

| Type | Role |
| --- | --- |
| `RequestTimeoutPolicy` | The policy value: `Timeout` (null = disabled), `StatusCode` (default 504), `WriteProblemDetails`, `WriteResponse` |
| `RequestTimeoutMetadata` | Sealed endpoint-metadata carrier for a policy; `Disabled` opts an endpoint out. Names `UseRequestTimeouts` as required middleware (`IRouteMiddlewareMetadata`) when it carries a timeout |
| `RequestTimeoutOptions` | Middleware options: `DefaultPolicy`, `TimeProvider`, `SuspendWhenDebuggerAttached` |
| `IRequestTimeoutFeature` | Per-exchange feature: `Token`, `Disable()`, `SetTimeout(TimeSpan)` |
| `WebApplicationExtensions` | `UseRequestTimeouts(...)` pipeline verbs |
| `RequestTimeoutRouteConventionExtensions` | `WithRequestTimeout(TimeSpan)`, `WithRequestTimeout(RequestTimeoutPolicy)` and `DisableRequestTimeout()` on a mapped route or a route group |

## Dependencies

`Assimalign.Cohesion.Web` (pipeline seams) · `Assimalign.Cohesion.Web.Routing` (published route
match, endpoint metadata, dispatch acknowledgement) · `Assimalign.Cohesion.Web.ProblemDetails` (timeout payload) ·
`Assimalign.Cohesion.Http.Streaming` (response-started probe). Per the Web-area dependency rule
it references no hosting module, holds no DI/configuration/logging state, and is delivered to
applications through the `App.Web` shared framework.

Design rationale — including why expiry does **not** trip the transport cancel while the
response is writable — lives in [DESIGN.md](DESIGN.md).
