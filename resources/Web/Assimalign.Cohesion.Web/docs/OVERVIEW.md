# Assimalign.Cohesion.Web — Overview

The Web area root: the pipeline and composition abstractions every Web library builds
against, plus the small set of middleware that is foundational to the rest of the
pipeline rather than a feature of its own.

## Scope

- **Pipeline contracts** — `IWebApplication`, `IWebApplicationBuilder`,
  `IWebApplicationPipeline` / `IWebApplicationPipelineBuilder`,
  `IWebApplicationMiddleware`, the `WebApplicationMiddleware` delegate, and
  `IWebApplicationServer`. Feature libraries (`Assimalign.Cohesion.Web.<Feature>`)
  compose against these seams; the runtime module (`Assimalign.Cohesion.Web.Hosting`)
  implements them. See `resources/Web/README.md` for the area dependency rule that keeps
  those two directions apart.
- **Forwarded-headers resolution** — the first-position middleware that resolves the
  effective client address/scheme/host behind proxies under an explicit trust model
  (`UseForwardedHeaders`, `ForwardedHeadersOptions`, the `ForwardedHeaders` header
  selection). It consumes the RFC 7239 / `X-Forwarded-*` parsing primitives from
  `Assimalign.Cohesion.Http` and publishes results as the `IHttpForwardedFeature`
  defined there. It lives in the root — not a feature package — because nearly every
  other pipeline concern (CORS, authentication, cookie policy, redirects, logging)
  consumes its output.

## Dependencies

`Assimalign.Cohesion.Http` only. The root deliberately has no DI, configuration, or
logging references — composition integration is `Web.Hosting`'s job.

## Usage

```csharp
app.UseForwardedHeaders(options =>
{
    options.Headers = ForwardedHeaders.XForwarded;              // what the proxy manages
    options.KnownNetworks.Add(IPNetwork.Parse("10.0.0.0/8"));   // who is allowed to forward
    options.ForwardLimit = 2;                                    // how deep the chain may go
});
// ...every identity-consuming middleware follows...
```

`UseForwardedHeaders` must be the **first** middleware registered — see the ordering
contract in [DESIGN.md](DESIGN.md). Downstream code reads the resolved identity through
`context.EffectiveScheme` / `context.EffectiveHost` / `context.EffectiveRemoteIp` /
`context.EffectiveRemoteEndPoint` (or the `IHttpForwardedFeature` directly); the raw
request headers and wire-level scheme/host/connection surfaces are never mutated.
