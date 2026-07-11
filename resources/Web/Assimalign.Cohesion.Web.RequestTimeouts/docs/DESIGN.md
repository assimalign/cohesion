# Assimalign.Cohesion.Web.RequestTimeouts — Design

## Design intent

Arm the per-exchange abort primitive the protocol core already ships (#703:
`IHttpContext.RequestCancelled` + `Cancel`/`CancelAsync`) with a *policy layer*: a builder-time
global default, per-endpoint overrides on the #150 endpoint-metadata seam, and an
ASP.NET-parity translation of expiry into a 504 (or a clean protocol-level abort once the
response has started). The package is a plain Web feature library under the middleware-first
composition model: one `UseRequestTimeouts` verb, values captured at builder time, no service
container, no request-time service location, no transport changes.

## Why expiry does NOT trip the transport cancel while the response is writable

The issue that motivated this package sketched "on expiry call `IHttpContext.CancelAsync()`
… write the configured status if the response has not started." Those two halves are mutually
exclusive on the wire, which is the load-bearing discovery of this design:

- `Cancel`/`CancelAsync` set the transport's `CancelRequested` flag, and **every transport's
  send path answers that flag by resetting the exchange instead of writing a response** —
  HTTP/1.1 writes nothing and ends the connection after the exchange
  (`Http1ConnectionContext.SendAsync`), HTTP/2 sends `RST_STREAM`, HTTP/3 resets the request
  stream. A 504 written after `CancelAsync()` is silently discarded.
- The transport token cannot be re-armed, and `IHttpContext.RequestCancelled` is get-only and
  transport-owned — there is no supported way to trip it *without* the reset semantics, and
  adding one would be a transport change (an explicit non-goal).

So the two intents are split by response state:

| State at expiry | Action |
| --- | --- |
| Response not started | Cancel downstream *work* via the linked token; write the policy's status/payload imperatively on the still-writable exchange; the transport sends it normally. `CancelRequested` is deliberately **not** set. |
| Response started (streamed head committed) | The status is already on the wire — `CancelAsync()` is the only clean answer: h2/h3 reset the stream, h1 truncates and closes. This is exactly what the #703 primitive is for. |

"Response started" is probed through `IHttpResponseStreamingFeature.HasStarted` when the
streaming feature package is composed; on the default buffered path the response cannot start
while the handler is still running, so the probe's absence correctly means "writable."

## Cancellation delivery: a decorated context, not a token swap

ASP.NET's middleware swaps `HttpContext.RequestAborted` through its request-lifetime feature.
Cohesion deleted its request-lifetime feature library (the #703 context members are its
successor), and `RequestCancelled` has no setter. The equivalent seam here is **decoration at
the middleware boundary**: `UseRequestTimeouts` passes downstream a pass-through `IHttpContext`
whose `RequestCancelled` is `linked(transport token, timeout token)`.

That single decoration makes every existing consumer timeout-aware with no contract changes:

- Handlers reading `context.RequestCancelled` observe the linked token.
- The router creates its per-dispatch handler token by linking off
  `context.RequestCancelled` — of the *decorated* context — so `IRouterRouteHandler`
  cancellation tokens trip too.
- `Cancel`/`CancelAsync`/`Features`/`Items` forward to the real context, so shared state
  never forks. The middleware retained the original context and writes the timeout response
  on it.

The linked token also trips on a genuine client abort, preserving the primitive's original
meaning downstream.

## Timeout attribution (client aborts are never mislabeled)

A downstream `OperationCanceledException` is converted to a timeout response only when
`timeout source fired && !transport token cancelled`. A client abort therefore propagates
unchanged — the server loop already treats an escaping OCE as a clean per-connection drain —
and when both race, the client abort wins (nothing can be delivered anyway). A handler that
swallows the cancellation and completes keeps the response it produced, matching ASP.NET. The
filter deliberately keys off the middleware's own state, not
`OperationCanceledException.CancellationToken`, because the throwing token is usually a
*further-linked* token (e.g. the router's per-dispatch source), not ours.

## Per-endpoint policy: observing the route-match publication

Cohesion's router **matches and dispatches in one middleware** — on a match it invokes the
handler and never calls `next`. There is no pipeline position "between match and dispatch" for
a policy consumer to occupy (the position ASP.NET's timeout middleware occupies between
`UseRouting` and endpoint execution). The documented routing contract fills the gap: *the
router installs `IRouteMatchFeature` on `IHttpContext.Features` before invoking the handler*.
The decorated context's feature collection forwards everything and reacts to that one
installation, applying the endpoint's `RequestTimeoutMetadata` (last-wins over the bag) at
exactly the match→dispatch boundary.

Alternatives rejected:

- **Match again inside the timeout middleware** (`IRouter.Match` is public): correct but pays
  the full route-matching cost twice per request.
- **Resolve the endpoint policy only when the global timer fires:** cannot honor an endpoint
  policy *shorter* than the global default (the global timer fires too late), and cannot arm
  anything when no global default exists.
- **Teach the router about timeouts** (arm around `Handler.InvokeAsync`): routing routes; a
  cross-cutting policy inside the router is the wrong ownership and would splinter the policy
  surface across two packages.

When routing eventually splits match from dispatch (the #28 evolution), the observation
collapses into a plain read of the match feature between the phases; the public surface is
unaffected.

The endpoint timer is measured **from the match**, not from request start — the endpoint's
budget belongs to its handler, not to route-table evaluation. `SetTimeout` re-arms from the
moment of the call, like `CancellationTokenSource.CancelAfter`.

## The timer: one unarmed CTS per exchange, TimeProvider-bound

Each governed exchange owns two sources: a timeout source constructed
`new CancellationTokenSource(Timeout.InfiniteTimeSpan, options.TimeProvider)` and the linked
source described above. Creating the timeout source *unarmed but with the provider* is
deliberate:

- the `(delay, TimeProvider)` constructor is what binds `CancelAfter` to the provider — a
  bare CTS re-armed later would silently fall back to system timing;
- the source must exist before a deadline is known, because a per-endpoint policy or a
  handler's `SetTimeout` can arm it when there is no global default;
- disable is `CancelAfter(InfiniteTimeSpan)` — the same one-timer re-arm as every other
  transition, so there is no timer allocation churn per policy change.

`CancelAfter` after the source has fired is inherently a no-op, which yields the documented
race semantic of `Disable`/`SetTimeout` (effective only before expiry) — the same race ASP.NET
documents for `DisableRequestTimeout`. Cost when the middleware is registered but nothing arms:
two small allocations per request, comparable to the linked source the router itself creates
per dispatch.

## Policy and metadata shape

- `RequestTimeoutPolicy` is an immutable init-only value object; a `null` `Timeout` *is* the
  disabled spelling (`RequestTimeoutPolicy.Disabled` is the shared instance). Disable is policy
  **data**, not an attribute — attributes would need reflection or a translation layer under
  AOT, and the metadata bag already gives last-wins override composition for free.
- `RequestTimeoutMetadata` is a **sealed concrete carrier with no interface** per the repo's
  metadata-carrier discipline (`RouteNameMetadata`/`RouteHostMetadata` precedent): the sealed
  type is the contract, guaranteeing the validated, immutable policy consumers read.
- An endpoint policy **replaces** the effective policy outright (timeout *and* response
  members); policies do not merge member-by-member — merging invites "where did this status
  come from" archaeology.
- The timeout response: `WriteResponse` (imperative, owns everything) beats
  `WriteProblemDetails` (RFC 9457 payload via `Web.ProblemDetails`) beats the bare status.
  Before writing, staged response state is reset (headers cleared, buffered body truncated) —
  the imperative analog of ASP.NET's `Response.Clear()`.

## Feature lifecycle

`IHttpRequestTimeoutFeature` is installed on the *real* feature collection for the duration of
the middleware scope and removed before its cancellation sources are disposed, so later
pipeline stages can never resolve a feature with disposed state. When the middleware is not
registered — or is suspended for an attached debugger — no feature exists and
`Features.Get<IHttpRequestTimeoutFeature>()` returns `null`, which is the discoverable
"no timeout governance" signal.

## Ordering and composition constraints

- `UseRequestTimeouts` must be registered **before** `UseRouting` (and before anything
  long-running it should govern): the middleware wraps its downstream, and endpoint policies
  are observed only when routing runs inside the timeout scope.
- Registration is expected once per pipeline. Nesting is not harmful (the innermost scope's
  token is what downstream observes) but has no defined use.
- Debugger suspension (`Debugger.IsAttached`, checked per request) skips the entire scope —
  no timer, no decoration, no feature — mirroring ASP.NET.

## AOT posture

No reflection anywhere: policy resolution is an `is`-test scan over the metadata bag, the
problem payload rides `Web.ProblemDetails`' `Utf8JsonWriter`-based writer, timers are
`TimeProvider`/`CancellationTokenSource` plumbing, and the feature is resolved by type test.
Nothing in the package (or its tests) needs dynamic code.

## Non-goals

- **No connection/parse-phase timeouts.** Keep-alive, request-head, and body data-rate
  enforcement are transport limits (#791/#810, `HttpServerLimits`) — this package governs
  *application execution time* only, from the moment the exchange enters the middleware.
- **No attribute surface.** Policies attach as metadata objects at map time; an attribute
  translation belongs to whatever source-generated mapping layer arrives later (#796).
- **No per-route timer wheel or shared scheduler.** One CTS per governed exchange is the
  simplest correct thing; optimize only with evidence.
- **No 504 after the response has started** — physically impossible; the started path aborts.
- **No Microsoft.Extensions dependencies, no separate hosting wiring.** The package composes
  purely against the Web root's builder seams and ships to applications via `App.Web`.
