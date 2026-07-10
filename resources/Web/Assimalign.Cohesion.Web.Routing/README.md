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
- Applies **inline constraints** that both validate and, for type constraints (`int`, `long`,
  `decimal`, `double`, `float`, `bool`, `guid`, `datetime`), **convert** the value to its CLR type —
  parsed once, invariant culture — plus text/value validators (`length`, `minlength`, `maxlength`,
  `min`, `max`, `range`, `regex`, `alpha`, `when`).
- Carries an immutable, typed **endpoint-metadata bag** on each route and surfaces the
  **route-match result** (route + typed values + metadata) as a strongly-typed HTTP feature — the
  reflection-free seam that auth, docs, and observability consume.
- Keeps routing state **per application** (no process-wide shared builder), so multiple web
  applications hosted in one process have fully isolated route tables.
- Generates **outbound URLs** (`ILinkGenerator`): routes register a unique, case-insensitive
  **name** via metadata (duplicates fail at build time), and paths or absolute URIs are generated
  from a name — or from route values alone, resolved by outbound precedence — honoring defaults,
  collapsing omitted optionals/catch-alls, re-validating constraints, escaping per path segment,
  and appending surplus values as a query string.

## Key types

| Type | Role |
|------|------|
| `RoutePattern` / `RoutePatternParser` | Parsed, immutable template shape and its parser. |
| `RoutePrecedence` | Computes inbound (match) and outbound (URL-gen) precedence. |
| `Route` | A pattern + the HTTP methods it accepts + a handler + its endpoint metadata. |
| `Router` | Evaluates routes by precedence; produces a `RouteMatch`. |
| `RouteMatch` / `RouteMatchStatus` | The match outcome: `Matched`, `MethodNotAllowed`, `NoMatch`. |
| `RouteParameterPolicy` / `TypedRouteParameterPolicy` | Inline constraint base types. `TypedRouteParameterPolicy` validates **and** converts (parse-once); the public extension point for custom typed constraints. |
| `RouteParameterPolicyMap` | Resolves inline policy names (`int`, `guid`, `length(n)`, `min(n)`, `range(a,b)`, …) to executable policies; `CreateDefault()` registers the built-ins. |
| `IRouterRouteMetadataCollection` / `RouterRouteMetadataCollection` | Immutable, ordered, reflection-free endpoint-metadata bag (`GetMetadata<T>` is last-wins). |
| `IRouteMatchFeature` | The per-request feature carrying the matched route, its values, and its metadata. |
| `IRouteNameMetadata` / `RouteNameMetadata` | Endpoint metadata naming a route for URL generation; unique per router, checked at build time. |
| `ILinkGenerator` | Outbound URL generation (`GetPathByName`, `GetUriByName`, `TryGetPathByValues`, …); exposed as `IRouter.LinkGenerator` and via `context.GetLinkGenerator()`. |
| `HttpContextRoutingExtensions` | `SetRouteMatch` / `GetRouteMatch` / `TryGetRoute` / `TryGetRouteValues` / `GetEndpointMetadata`(`<T>`) / `GetLinkGenerator` over the routing features. |
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

Outbound, a route named through its metadata generates URLs back out of the same table:

```csharp
var route = new Route(
    new[] { HttpMethod.Get }, RoutePatternParser.Parse("/users/{id:int}"),
    RouteParameterPolicyMap.CreateDefault(), userHandler,
    new RouterRouteMetadataCollection(new RouteNameMetadata("user")));
var router = new Router(new IRouterRoute[] { route });   // duplicate names throw here

string path = router.LinkGenerator.GetPathByName(
    "user", new RouteValueDictionary { ["id"] = 42, ["expand"] = "orders" });
// => /users/42?expand=orders   (constraints re-validate; surplus values become the query)

string uri = router.LinkGenerator.GetUriByName(
    "user", HttpScheme.Https, new HttpHost("example.com"), new RouteValueDictionary { ["id"] = 42 });
// => https://example.com/users/42
```

In request handlers, resolve the application's generator with `context.GetLinkGenerator()`.

## Design

See [docs/DESIGN.md](docs/DESIGN.md) for the matcher pipeline, the precedence scheme, the
405-vs-404 model, the HEAD→GET fallback, AOT posture, and the routing features delivered by
sibling issues (metadata, groups, link generation, source-gen binding).
