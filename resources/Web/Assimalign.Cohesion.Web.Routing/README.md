# Assimalign.Cohesion.Web.Routing

Deterministic, standards-aware HTTP route matching for the Cohesion Web stack. This is an
**L3** service-platform library: it consumes the L1 `Assimalign.Cohesion.Http` protocol
primitives and is consumed by the higher-level web programming models (API, functions,
controllers, metadata, results).

## What it does

- Parses route templates (`/users/{id:int}/assets/{name}.{ext?}`, catch-alls, defaults,
  inline constraints) into an immutable `RoutePattern`.
- Matches an inbound request against a set of routes with correct **precedence** (a literal
  segment beats a parameter segment regardless of registration order).
- Distinguishes **404** (no route matched the path) from **405** (a route matched the path
  but not the method), emitting an RFC 9110 `Allow` header for the latter.
- Accepts **multiple HTTP methods** per route and serves **HEAD** from a matching **GET**
  route (RFC 9110 §9.3.2).
- Carries an immutable, typed **endpoint-metadata bag** on each route and surfaces the
  **route-match result** (route + values + metadata) as a strongly-typed HTTP feature — the
  reflection-free seam that auth, docs, and observability consume.

## Key types

| Type | Role |
|------|------|
| `RoutePattern` / `RoutePatternParser` | Parsed, immutable template shape and its parser. |
| `RoutePrecedence` | Computes inbound (match) and outbound (URL-gen) precedence. |
| `Route` | A pattern + the HTTP methods it accepts + a handler + its endpoint metadata. |
| `Router` | Evaluates routes by precedence; produces a `RouteMatch`. |
| `RouteMatch` / `RouteMatchStatus` | The match outcome: `Matched`, `MethodNotAllowed`, `NoMatch`. |
| `RouteParameterPolicy*` | Inline constraints (`int`, `range`, `regex`, required-value). |
| `IRouterRouteMetadataCollection` / `RouterRouteMetadataCollection` | Immutable, ordered, reflection-free endpoint-metadata bag (`GetMetadata<T>` is last-wins). |
| `IRouteMatchFeature` | The per-request feature carrying the matched route, its values, and its metadata. |
| `HttpContextRoutingExtensions` | `SetRouteMatch` / `GetRouteMatch` / `TryGetRoute` / `TryGetRouteValues` / `GetEndpointMetadata`(`<T>`) over that feature. |
| `RoutingExtensions.UseRouting` | Pipeline integration (dispatch / 405 / fall-through). |

## Usage

```csharp
var router = new Router(new IRouterRoute[]
{
    new Route(HttpMethod.Get, "/api/status", statusHandler),   // literal — wins for /api/status
    new Route(HttpMethod.Get, "/api/{id:int}", getHandler),    // constrained parameter
    new Route(new[] { HttpMethod.Get, HttpMethod.Post }, "/items", itemsHandler),
});

RouteMatch match = router.Match(context);
switch (match.Status)
{
    case RouteMatchStatus.Matched:          /* match.Route, match.Values */ break;
    case RouteMatchStatus.MethodNotAllowed: /* 405 + match.ToAllowHeaderValue() */ break;
    case RouteMatchStatus.NoMatch:          /* 404 */ break;
}
```

Within a web application pipeline, prefer `builder.UseRouting()`, which performs this dispatch
(invoke handler / emit 405 + `Allow` / fall through to the next middleware) for you.

## Design

See [docs/DESIGN.md](docs/DESIGN.md) for the matcher pipeline, the precedence scheme, the
405-vs-404 model, the HEAD→GET fallback, AOT posture, and the routing features delivered by
sibling issues (metadata, groups, link generation, source-gen binding).
