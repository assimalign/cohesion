# Assimalign.Cohesion.Web.Routing — Design

## Purpose

This library turns a set of registered route templates into a deterministic decision:
given an inbound `IHttpContext`, which handler (if any) should run, and — when none
should — whether that is a *no route* (404) or a *wrong method* (405) situation. It is a
foundation primitive: the API, function, controller, and metadata programming models
(issues #149, #150, #151, #786–#789, #796) all build on the matcher defined here, so the
matcher's behavior must be predictable and standards-aware before those layers are added.

Scope of this library:

- Route **template parsing** into an immutable `RoutePattern` (`Patterns/`).
- Route **parameter policies / constraints** (`Policies/`) evaluated during matching.
- A **route** (`Route`) that binds a pattern, the HTTP methods it accepts, and a handler.
- A **router** (`Router`) that evaluates routes against a request with correct precedence
  and HTTP method semantics.
- Minimal **pipeline integration** (`UseRouting`) so a web application can dispatch through
  the router.

## The matcher pipeline

```
raw template ──RoutePatternParser──▶ RoutePattern ──┐
                                                     ├─▶ Route ──┐
     HttpMethod[] ─────────────────────────────────-┘           ├─▶ Router ──▶ RouteMatch
                                                                 │
     IRouterRouteHandler ────────────────────────────────────---┘
```

- **`RoutePattern`** is the parsed, immutable shape of a template: an ordered list of path
  segments (literals, separators, parameters), the parameters and their inline policies,
  defaults, required values, and the precomputed inbound/outbound precedence.
- **`Route`** pairs a pattern with the set of HTTP methods it accepts and the handler to
  invoke. Matching is split into two phases (see below).
- **`Router`** owns the collection of routes and produces a `RouteMatch` for a request.

### Two-phase matching (path, then method)

`IRouterRoute` exposes matching as two operations rather than one:

- `TryMatchPath(context, out values)` — matches the request **path** and validates parameter
  policies, **ignoring** the HTTP method.
- `TryMatch(context, out values)` — the full match: `TryMatchPath` **and** the request method
  is accepted.

Splitting the phases is what lets the router tell a genuine 404 apart from a 405. If any route
matches the path but none accepts the method, the correct response is `405 Method Not Allowed`
with an `Allow` header — not `404 Not Found`. A single combined predicate (the original design)
collapsed both into "no match" and could never produce a 405. This split is the seam the whole
405-vs-404 behavior hangs on.

## Precedence ordering (fix for the insertion-order defect)

Each `RoutePattern` carries an `InboundPrecedence` (a `decimal`) computed by `RoutePrecedence`.
Every segment contributes one digit at a decreasing decimal place, where a **lower** digit is
**more specific**:

| Segment kind                         | Digit |
|--------------------------------------|-------|
| Literal                              | 1     |
| Constrained parameter / multi-part   | 2     |
| Unconstrained parameter              | 3     |
| Constrained catch-all                | 4     |
| Unconstrained catch-all              | 5     |

So `/api/status` = `1.1`, `/api/{id:int}` = `1.2`, `/api/{id}` = `1.3`. **Lower is more
specific and must be evaluated first.**

The original `Router` computed precedence but never used it — it matched routes in **insertion
order** and returned the first hit. That meant registering `/api/{id}` before `/api/status`
shadowed the literal: a request for `/api/status` matched the parameter route. `Router` now
sorts its candidates **once at construction** by ascending `InboundPrecedence`, breaking ties by
**registration order** (a stable, deterministic total order via the `PrecedenceKey` comparer).
`Routes` still enumerates in registration order; only the internal evaluation list is reordered.

Method acceptance can still override raw precedence: the router walks candidates in precedence
order and returns the first whose path **and** method both match. A higher-precedence route that
matches the path but not the method does not block a lower-precedence route that matches both —
it only contributes to the `Allow` set if nothing ends up matching.

## HTTP method semantics

### Multiple methods per route

A `Route` accepts a **set** of methods (`Methods`), not a single one. Constructors accept either
a single `HttpMethod` (the common case) or an `IEnumerable<HttpMethod>`. Duplicate methods are
de-duplicated at construction. An **empty** method set means "accept any method" — useful for
catch-all/fallback routes.

### 405 vs 404 and the `Allow` header

`Router.Match` returns a `RouteMatch` with one of three `RouteMatchStatus` values:

- `Matched` — a route matched path and method; `Route` and `Values` are populated.
- `MethodNotAllowed` — one or more routes matched the path but none accepted the method;
  `AllowedMethods` holds the union of methods those routes accept, and `ToAllowHeaderValue()`
  formats them as an RFC 9110 `Allow` header (`"GET, POST, HEAD"`).
- `NoMatch` — nothing matched the path.

`RouteMatch` is a `readonly struct` so the common path allocates nothing beyond the captured
route values.

### HEAD falls back to GET

Per RFC 9110 §9.3.2 a `HEAD` request is answered identically to `GET` (headers only, no body).
A route that maps `GET` but not `HEAD` therefore **accepts** `HEAD`: the router's method-
acceptance check treats `HEAD` as satisfied by a `GET` mapping when `HEAD` is not explicitly
mapped. Symmetrically, a `405` `Allow` header for a `GET`-capable path advertises `HEAD`. An
explicit `HEAD` route still wins for `HEAD` requests, and a `HEAD`-only route does **not** answer
`GET`.

The fallback lives in `Router`, not in `Route`. `Route.TryMatch`/`AcceptsMethod` report *exact*
membership so the route's declared surface stays honest; the router layers the RFC synthesis on
top. This keeps `Methods` a truthful description of what was registered while still serving HEAD
correctly end-to-end.

## Pipeline integration (`UseRouting`)

The `UseRouting` middleware resolves the `IRouterFeature`, calls `router.Match(context)` once,
and dispatches on the result:

- `Matched` → store the match on the context (`SetRouteMatch`) and invoke the handler
  (terminal; downstream middleware does not run).
- `MethodNotAllowed` → set `405` and the `Allow` header, then **short-circuit** (do not fall
  through to the terminal 404 pipeline).
- `NoMatch` → call `next`, letting the rest of the pipeline (and any terminal 404) handle it.

`IRouter.RouteAsync` performs the same dispatch for callers that use the router directly without
the middleware, so a direct `RouteAsync` also produces a correct 405 with `Allow`.

## Parameter policies (constraints)

Inline constraints (`{id:int}`, `{id:range(1,10)}`, `{id:regex(...)}`, required-value, …) are
resolved through a `RouteParameterPolicyMap` and evaluated **inside** `TryMatchPath`. A failed
constraint means the path did not match *for that route*, so a more general route can still pick
the request up (e.g. `/api/{id:int}` rejects `/api/abc`, which then falls through to
`/api/{id}`). Unknown policy references fail fast at `Route` construction, not at request time.

## AOT posture

- No reflection, no runtime code generation, no dynamic activation anywhere in the match path —
  the library is `IsAotCompatible` and trim-safe.
- Parameter policies are explicit objects resolved through a map, not reflected constructors.
- Source-generated endpoint **binding** (turning a matched route into typed handler arguments)
  is intentionally out of scope here and is delivered by the analyzer work in #796; the matcher
  produces a `RouteValueDictionary` of strings and lets that layer bind.

## Lifecycle and immutability

- `RoutePattern` and `Route` are immutable once constructed.
- `Router` snapshots its routes into an immutable list and precomputes the precedence-ordered
  evaluation array in its constructor. A router instance is therefore safe to share across
  concurrent requests; there is no per-request mutable router state.
- `RouteMatch` is an immutable value; the only mutable output is the per-request
  `RouteValueDictionary`.

## Non-goals (delivered elsewhere in the routing epic #28)

- **Endpoint metadata bag** — #150 (`IRouterRoute` metadata seam that auth/CORS/OpenAPI consume).
- **Typed route values, richer constraints, per-app router state** — #789.
- **Route groups (`MapGroup`)** — #786.
- **Named routes + link generation (outbound URL building)** — #787. `OutboundPrecedence` is
  computed and preserved for this future work but is not consumed by the matcher.
- **Host-based matching (`RequireHost`)** — #788.
- **Source-generated binding + validation** — #796.
- **Result writers / content negotiation** — #149.

## Standards

- **RFC 3986** — URI path syntax (segment splitting, percent-encoding expectations).
- **RFC 9110** — HTTP semantics: §9.3.2 (HEAD as GET), §15.5.6 (405 + `Allow`), method
  case-sensitivity.
