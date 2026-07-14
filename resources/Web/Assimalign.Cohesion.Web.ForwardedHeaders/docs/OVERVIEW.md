# Assimalign.Cohesion.Web.ForwardedHeaders — Overview

Forwarded-headers resolution for Cohesion web applications: the first-position
middleware that resolves the effective client address/scheme/host behind proxies under
an explicit trust model, and the builder verb that composes it.

## Scope

- **`UseForwardedHeaders`** — the pipeline verb (on `IWebApplicationPipelineBuilder`)
  that validates and snapshots the trust model at composition time and registers the
  resolving middleware.
- **`ForwardedHeadersOptions`** — the trust model: `Headers`
  (`ForwardedHeaderNames` header selection), `KnownProxies` / `KnownNetworks` (CIDR),
  `ForwardLimit`, and `TrustLocalTransports` for non-IP (Unix domain socket /
  named-pipe / in-memory) peers.
- **The rightmost-first trust walk** — consumes the RFC 7239 `Forwarded` and
  `X-Forwarded-For/Proto/Host` parsing primitives from `Assimalign.Cohesion.Http`
  (never re-parses header text) and publishes the outcome as the
  `IHttpForwardedFeature` defined in `Assimalign.Cohesion.Http.Forwarded`.

## Dependencies

`Assimalign.Cohesion.Web` (pipeline seams) and `Assimalign.Cohesion.Http.Forwarded`
(the output contract), which brings the core `Assimalign.Cohesion.Http` primitives.
No DI, configuration, or logging — registration is dependency-free per the Web-area
rules.

## Usage

```csharp
app.UseForwardedHeaders(options =>
{
    options.Headers = ForwardedHeaderNames.XForwarded;          // what the proxy manages
    options.KnownNetworks.Add(IPNetwork.Parse("10.0.0.0/8"));   // who is allowed to forward
    options.ForwardLimit = 2;                                    // how deep the chain may go
});
// ...every identity-consuming middleware follows...
```

`UseForwardedHeaders` must be the **first** middleware registered — see the ordering
contract in [DESIGN.md](DESIGN.md). Downstream code reads the resolved identity through
`context.EffectiveScheme` / `context.EffectiveHost` / `context.EffectiveRemoteIp` /
`context.EffectiveRemoteEndPoint` (from `Assimalign.Cohesion.Http.Forwarded`) or the
`IHttpForwardedFeature` directly; the raw request headers and wire-level
scheme/host/connection surfaces are never mutated.
