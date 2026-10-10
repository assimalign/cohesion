# Assimalign.Cohesion.Web.Routing

Deterministic, standards-aware HTTP route matching for the Cohesion Web stack. This is an
**L3** service-platform library: it consumes the L1 `Assimalign.Cohesion.Http` protocol
primitives and is consumed by the Web API surface: `Web.Api`'s source-generated typed endpoints,
endpoint metadata consumers (rate limiting, request timeouts, output caching, logging), and link
generation. The Web area is middleware-first; the controller, function and result programming
models were set aside on 2026-07-10.

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
  `min`, `max`, `range`, `regex`, `alpha`, `when`, `nonfile`).
- Maps **fallback routes** (`MapFallback`): the lowest-precedence `GET`/`HEAD` route for paths no
  other route matches, never answering a file-name path (`nonfile`) and never turning an unmatched
  path into a 405. `MapFallbackToFile` in `Web.StaticFiles` builds the single-page-application shape on it.
- Composes **route groups** (`MapGroup`) at builder time. A prefix (which may itself contain
  parameters, e.g. `{tenant}/api`) and shared parameter policies are merged onto each child route
  **at registration**, so grouped routes match at exactly the cost of directly-mapped ones and
  participate normally in precedence. Shared endpoint metadata is composed when the route table is
  built, so it reaches every child regardless of call order.
- Returns an **endpoint convention builder** from every template `Map` (`IRouterRouteBuilder`), so
  per-route metadata and policy verbs (`WithMetadata`, `WithName`, `RequireHost`, and feature verbs
  such as `RequireRateLimiting`) attach where the route is mapped (#1055).
- Carries an immutable, typed **endpoint-metadata bag** on each route and surfaces the
  **route-match result** (route + typed values + metadata) as a strongly-typed HTTP feature — the
  reflection-free seam that auth, docs, and observability consume.
- Supports **host-constrained routes** (exact hosts, `*.wildcard` subdomains, `host:port`,
  IPv6 literals) declared as endpoint metadata and evaluated during candidate selection:
  non-matching hosts fall through to other candidates, and host-constrained routes outrank
  unconstrained ties. The host matched is the effective one (`context.EffectiveHost` from
  `Http.Forwarded`): the host a trusted proxy forwarded when `UseForwardedHeaders` runs ahead of
  routing, otherwise the wire host. Either way the client asserts it, so a host constraint selects
  a route and never protects one: guard internal endpoints with authorization or the connection's
  local endpoint (DESIGN, "Not an access control").
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
| `IRouterGroupBuilder` / `RouterBuilderExtensions.MapGroup` | Builder-time route groups: prefix + shared policies composed onto children at registration, shared metadata composed at build; nestable; child-over-group overrides. |
| `IRouterConventionBuilder` / `IRouterRouteBuilder` | Endpoint convention builders: `WithMetadata` on one route (returned by every template `Map`) or on a group; composed when the route table is built. |
| `RouterConventionBuilderExtensions` | Routing's convention verbs: `WithName` (routes) and `RequireHost` (routes and groups). |
| `RouteHostConstraint` | Parsed host constraint (`host[:port]`, `*.wildcard`, `*`, bracketed IPv6) with `Parse`/`TryParse`/`IsMatch`. |
| `RouteHostMetadata` | Sealed endpoint-metadata carrier declaring the hosts a route accepts; consulted by `Router` during candidate selection. |
| `IRouteMatchFeature` | The per-request feature carrying the matched route, its values, and its metadata (and `IsPreflight` for a CORS preflight's candidate). It is also the exchange's `IWebEndpointFeature`, which the pipeline terminal runs. |
| `IRouteMiddlewareMetadata` | Endpoint metadata that names the middleware which must honor it; dispatch fails the request when that middleware never acknowledged the endpoint (`AcknowledgeEndpointMiddleware`). |
| `RouteNameMetadata` | Sealed endpoint-metadata carrier naming a route for URL generation; unique per router, checked at build time. |
| `ILinkGenerator` | Outbound URL generation (`GetPathByName`, `GetUriByName`, `TryGetPathByValues`, …); exposed as `IRouter.LinkGenerator` and via `context.GetLinkGenerator()`. |
| `HttpContextRoutingExtensions` | `SetRouteMatch` / `GetRouteMatch` / `TryGetRoute` / `TryGetRouteValues` / `GetEndpointMetadata`(`<T>`) / `AcknowledgeEndpointMiddleware` / `GetLinkGenerator` over the routing features. |
| `RoutingExtensions.UseRouting` | Pipeline integration: selects the endpoint (match / 405 / preflight candidate / none) and calls `next`; the terminal runs it. |
| `IWebEndpointFeature` | The endpoint selected for the exchange: the delegate the terminal runs and the `RouteTemplate` the server reports as `http.route`. Moved here from the Web root (#1379). |
| `WebApplicationTerminal` | The standard pipeline terminal: runs the published endpoint, or answers an untouched response with a bodyless 404. `WebApplication` and every non-rejoining branch end in it (#1379). |
| `WebApplicationBranchingExtensions` / `IWebPathBaseFeature` | `Map(path, branch)` and `MapWhen` (branches that end in the terminal), and the path-base view a path branch publishes (`GetPathBase()`, `GetEffectivePath()`). Moved here from the Web root (#1379); the rejoining `UseWhen` and `Run` stay in the root. |

## Usage

```csharp
var router = new Router(new IRouterRoute[]
{
    new Route(HttpMethod.Get, "/api/status", statusHandler),   // literal — wins for /api/status
    new Route(HttpMethod.Get, "/api/{id:int}", getHandler),    // constrained parameter
    new Route(new[] { HttpMethod.Get, HttpMethod.Post }, "/items", itemsHandler),
});

// Or compose grouped endpoints on a builder. Every template Map returns the route's builder, and
// group metadata reaches every child whenever it is attached (it composes at build):
var builder = new RouterBuilder();
IRouterGroupBuilder v1 = builder.MapGroup("api/v1");
v1.Map(HttpMethod.Get, "orders/{id:int}", getOrder)   // matches /api/v1/orders/{id:int}
  .WithName("order");                                 // route-level metadata
v1.Map(HttpMethod.Get, "", index);                    // matches /api/v1
v1.RequireHost("api.example.com");                    // applies to both children

RouteMatch match = router.Match(context);
switch (match.Status)
{
    case RouteMatchStatus.Matched:          /* match.Route, match.Values */ break;
    case RouteMatchStatus.MethodNotAllowed: /* 405 + match.ToAllowHeaderValue() */ break;
    case RouteMatchStatus.NoMatch:          /* 404 */ break;
}
```

Within a web application pipeline, prefer `builder.UseRouting()`. It **selects** the endpoint and
calls `next`, and the pipeline's terminal runs the endpoint (or answers 405 with `Allow`, or 404).
Middleware registered after `UseRouting` therefore runs with the matched endpoint and its metadata
known (`context.GetEndpointMetadata<T>()`). Register policy middleware such as `UseRateLimiting`,
`UseRequestTimeouts` and `UseOutputCache` there. A CORS preflight resolves the candidate endpoint
for the requested method (`IRouteMatchFeature.IsPreflight`) without running it. Map every route
before the application starts: the router is built once, when the pipeline is built at startup, so
an invalid route table (such as a duplicate route name) fails the start, and mapping a route
afterwards throws `InvalidOperationException`.

> **Migrating from terminal routing (#1054).** `UseRouting` used to run a matched route's handler
> itself, so middleware registered after it ran only for unmatched requests. It now runs for every
> request, and the handler runs at the terminal. See "Migration from terminal routing" in
> [docs/DESIGN.md](docs/DESIGN.md).

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
