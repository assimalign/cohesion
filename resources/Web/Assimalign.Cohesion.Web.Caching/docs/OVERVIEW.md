# Assimalign.Cohesion.Web.Caching — Overview

Server-owned HTTP **output caching** for the Cohesion Web pipeline: a middleware plus policy layer that
serves a stored response to a repeat request **without invoking the endpoint**, over an async, tag-aware
store built on Cohesion's owned cache foundation. It is the middleware consumer of the RFC 9111 typed
primitives from `Assimalign.Cohesion.Http` (#755) — it re-implements no header parsing.

## Purpose and scope

- **Cache cacheable GET/HEAD responses** at the server, needing no client participation — the standard
  enterprise capability for high-traffic API and page workloads.
- **Policy model at builder time**: a base policy applied to every request, named policies, and
  per-endpoint overrides through a sealed metadata carrier resolved at the router's route-match seam.
- **Correct cache-or-bypass decisions** via the #755 typed `Cache-Control` primitives: bypass on
  `no-store`/`private`/`Set-Cookie`, on non-safe methods, and on non-`200` responses; never cache
  authenticated responses by default.
- **Vary-correct**: the stored response's own `Vary` header partitions the cache key (RFC 9111 §4.1), so
  a compressed or content-negotiated variant is never served to a client that cannot accept it.
- **Tag-based invalidation** reachable from application code.

## Dependencies

- `Assimalign.Cohesion.Web` — the root pipeline-builder seam the `UseOutputCache` verb composes against.
- `Assimalign.Cohesion.Web.Routing` — the router match (endpoint discovery) and the endpoint-metadata seam.
- `Assimalign.Cohesion.Http` — the `IHttpContext` surface and the #755 `HttpCacheControl` / `HttpFreshness`
  primitives.
- `Assimalign.Cohesion.Http.Streaming` — the `IHttpResponseStreamingFeature.HasStarted` guard.
- `Assimalign.Cohesion.Caching` / `Assimalign.Cohesion.Caching.InMemory` — the synchronous cache
  foundation the default in-memory store adapts.

It never references `Assimalign.Cohesion.Web.Hosting` (the resource hosting-isolation rule, `COHRES001`).

## Usage

```csharp
// Base-policy mode: cache every GET/HEAD for one minute.
app.UseOutputCache(options => options.AddBasePolicy(policy => policy.Duration = TimeSpan.FromMinutes(1)));

// Opt-in mode: no base policy; cache only endpoints that ask for it.
app.UseOutputCache(options => options.AddPolicy("catalog", policy =>
{
    policy.Duration = TimeSpan.FromMinutes(5);
    policy.VaryByQuery("page");
    policy.Tag("catalog");
}));

var routes = app.UseRouting();
routes.Map(new Route(HttpMethod.Get, "/catalog",
    new RouterRouteHandler(GetCatalog),
    new RouterRouteMetadataCollection(new OutputCacheMetadata("catalog"))));

// Invalidate by tag when the underlying data changes.
await store.EvictByTagAsync("catalog");
```

Register `UseOutputCache` **before** `UseResponseCompression`, any content-negotiated write, and
`UseRouting` (see `DESIGN.md` for why the ordering is load-bearing).

## Documentation

- `docs/DESIGN.md` — the store seam, policy model, bypass matrix, the Vary decision and ordering rationale,
  the QUERY posture, size accounting, and non-goals.
