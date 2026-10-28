# Assimalign.Cohesion.Web.RateLimiting — Design

## Design intent

Inbound rate limiting is table stakes for an enterprise web server, and the platform already carries the
asset it should be built on: the BCL `System.Threading.RateLimiting` package (pinned `10.0.0`, AOT-safe),
which supplies all four limiter algorithms — fixed window, sliding window, token bucket, concurrency —
plus partitioning and queueing. This package therefore supplies only the **Web-pipeline surface**: the
middleware, the policy model, the partition-key selectors, and the rejection response. It **never
reimplements a limiter** — no token bucket, no window accounting. That boundary is the whole point (issue
#783): Cohesion adapts the BCL primitives, matching the Resilience area's stated intent for the
client-side limiter (`Assimalign.Cohesion.Resilience.RateLimiting`).

The package owns: two options-carried hooks, one options object, one builder (`RateLimitingBuilder`, whose
registration verb `builder.Services.AddRateLimiting` is a generated component integration), one sealed
policy type, one sealed metadata carrier, a static partition-key helper, a typed feature, and one
parameterless `extension(IWebApplicationPipelineBuilder)` verb that places the middleware. Everything
request-time is an `AcquireAsync` against a prebuilt limiter and a status write.

## The policy model — a global limiter plus named policies, additive

`RateLimitingOptions` carries a `GlobalPolicy` and a set of named policies (`AddPolicy`). A
`RateLimitingPolicy` is a `PartitionedRateLimiter<IHttpContext>` plus a permit count — built once, when
the application registers rate limiting (`RateLimitingBuilder.Options`).

- **The global limiter is the must-have.** It is applied to **every** request that reaches the
  middleware and acquired **up-front**, before any downstream work, with the limiter's **full queueing
  semantics** (`AcquireAsync`). It is the primary flood shield, and it needs no endpoint, so it works
  wherever the middleware is registered.
- **Named policies attach per-endpoint** through `RateLimitingMetadata` in the routing metadata bag. A
  per-endpoint policy is evaluated **in addition to** the global limiter — **both must grant a lease** —
  and is acquired the same way, asynchronously and with queueing.

### Why additive, not replace

`Web.RequestTimeouts` (the sibling per-endpoint-metadata feature) has an endpoint policy **replace** the
global default. Rate limiting deliberately does **not**, because a rate limiter **spends a permit** at
acquisition, while a timeout only arms a timer:

- **The global limiter is the flood shield.** If endpoint metadata could replace it, one endpoint with a
  loose (or misconfigured) policy would carve a hole in the shield for everything routed to it.
- **The global limiter must not change meaning with its position.** An application that uses only the
  global limiter may keep the middleware ahead of `UseRouting`, where it runs before any endpoint is
  known and cannot consult endpoint metadata. Additive semantics are the only ones that hold in both
  positions.
- **Parity.** ASP.NET Core's `GlobalLimiter` is likewise combined with, not replaced by, endpoint
  limiters.

Before #1054 additivity was also forced: routing was terminal, so the global lease was always acquired
before the endpoint was known. Routing now publishes the endpoint first, so a replace model would be
implementable; it is rejected for the reasons above. `RateLimitingMetadata.Disabled` removes only the
**per-endpoint** gate; the global limiter still applies.

## Partition keys — trust composition and BCP 38

Partitioning is `PartitionedRateLimiter<IHttpContext>`-based, with AOT-safe selectors in
`RateLimitPartitionKeys`:

- **`ClientAddress`** reads `IHttpContext.EffectiveRemoteIp` — the `Http.Forwarded` (#778) effective
  client. When the forwarded-headers trust middleware has run and vouched for a proxy chain, this is the
  real client; otherwise it is the transport peer. Client-identity keying therefore **composes with the
  forwarded trust model for free**, and is the reason `Http.Forwarded` is a direct reference.
- **`Header`** reads a request header value — intended for values a **trusted gateway injects** (an API
  key, a tenant id).
- **A typed selector** — any `Func<IHttpContext, TKey>` — through `RateLimitingPolicy.Create`.

**BCP 38 caution (documented on the type and honored by the defaults):** never partition on **unvalidated
client-supplied** input. An attacker who can set the partition key (a spoofable `X-Forwarded-For`, an
unauthenticated header) can mint unlimited partitions and defeat the limit entirely. `ClientAddress` is
safe because it goes through the trust-gated effective identity, not the raw header; `Header` is
documented as gateway-trusted only.

## Per-endpoint mechanics — reading the published endpoint

`UseRouting` selects the endpoint and calls `next`; the pipeline's terminal runs it (#1054). A match is
published as an `IRouteMatchFeature` that every middleware registered after `UseRouting` can read. The
rate-limiting middleware sits between the two. It acquires the global lease first, then the endpoint's
own lease, then acknowledges the endpoint and calls `next`; a rejection at either gate answers the
request and stops the pipeline there, so the endpoint does not run.

```mermaid
flowchart TD
    Routing["UseRouting: publish the endpoint"] --> Global["Global limiter: AcquireAsync"]
    Global -->|"rejected"| Reject["429 and Retry-After; the pipeline stops"]
    Global -->|"admitted"| Endpoint["Endpoint policy from RateLimitingMetadata: AcquireAsync"]
    Endpoint -->|"rejected"| Reject
    Endpoint -->|"admitted"| Ack["Acknowledge UseRateLimiting; call next"]
    Ack --> Terminal["Pipeline terminal runs the endpoint"]
```

- **The endpoint's policy** is the published match's `RateLimitingMetadata`, read last-wins
  (`GetMetadata<RateLimitingMetadata>()`), so an endpoint-level declaration overrides a group-level one. A
  named policy is resolved against `rateLimiting.Options.AddPolicy`; an unknown name throws
  `InvalidOperationException`, a configuration error surfaced by the first request that reaches it.
- **Declared with convention verbs (#1055).** `RequireRateLimiting(name)`, `RequireRateLimiting(policy)`
  and `DisableRateLimiting()` are generic extension members over routing's `IRouterConventionBuilder`, so
  one verb serves a mapped route and a route group and returns the receiver's own builder type. Each
  appends a `RateLimitingMetadata`; routing composes it when the route table is built, outer group first,
  so the last-wins read above resolves the most specific declaration regardless of call order.
- **Asynchronous, with queueing.** The endpoint lease is acquired exactly like the global one:
  `AcquireAsync(context, permits, context.RequestCancelled)`. A queueing limiter holds the request until a
  permit frees up. A client that goes away while queued stops the wait: the `OperationCanceledException`
  propagates (the server treats it as a clean drain) and the endpoint never runs.
- **A rejection is an ordinary short-circuit.** The middleware writes the rejection and returns without
  calling `next`. No exception carries it, so no catch-all middleware can turn it into a 500.
- **CORS preflight.** Routing publishes the candidate endpoint of a CORS preflight with `IsPreflight`
  set; the candidate never runs for the preflight, which carries no credentials. The middleware neither
  applies the candidate's policy nor acknowledges it, so a preflight never spends an endpoint permit. The
  global limiter counts it like any other request.

### Fail closed when the middleware is missing or misordered

Before #1054 routing was terminal, so the middleware had to sit **ahead** of `UseRouting` and observe the
router installing its match through a feature-collection decorator. That seam was synchronous (`Set` is
`void`), which forced a non-queueing `AttemptAcquire` on the endpoint gate and an internal exception
signal to carry a rejection back to the middleware. Both are gone.

The old position is now the hazard. Placed ahead of `UseRouting`, the middleware sees no endpoint, so
endpoint policies would silently stop applying. `RateLimitingMetadata` therefore implements
`IRouteMiddlewareMetadata`: `RequiredMiddleware` is `UseRateLimiting`, or `null` for `Disabled`. The
middleware acknowledges every non-preflight endpoint it processes
(`context.AcknowledgeEndpointMiddleware("UseRateLimiting")`), whether or not it found a policy, because
routing checks every metadata item that names the middleware, including a group-level policy an
endpoint-level `Disabled` overrides. When routing dispatches an endpoint whose metadata names
`UseRateLimiting` and the request was never acknowledged, it throws `InvalidOperationException` naming the
endpoint and the middleware instead of running the endpoint without its limit. The global limiter needs no
endpoint and keeps working in either position.

## Queueing semantics

`QueueLimit`/`QueueProcessingOrder` pass through the BCL limiter options unchanged — the package configures
nothing about queueing itself. Both gates acquire with `AcquireAsync` and **honor queueing fully**: a
request waits for a permit up to the queue limit, and stops waiting when the request is cancelled. The
global lease is acquired first and held while the request waits in an endpoint policy's queue, so a queued
request still counts against a global concurrency limiter.

## Registration: `AddRateLimiting` configures, `UseRateLimiting` places (2026-10-28)

`builder.Services.AddRateLimiting(rateLimiting => ...)` is a generated component integration over
`RateLimitingBuilder`. It builds the middleware once, from a snapshot of `RateLimitingBuilder.Options` (the
global policy, the named-policy map, the rejection status, and both hooks), and registers it as a
container-owned `IWebApplicationMiddleware` singleton. The middleware reads its options on every request,
so the snapshot is what keeps a later change to the options, a later `AddPolicy` included, from reaching a
middleware already registered; the policies themselves are shared, so a policy keeps one limiter wherever
it is used. The builder adds no validation of its own: a policy rejects a non-positive permit count when
it is created and `AddPolicy` rejects a duplicate name, both inside the `AddRateLimiting` callback, so a
misconfiguration still fails at registration.

`UseRateLimiting()` takes no arguments. It places the last registration of the middleware it finds in
`IWebApplicationContext.Middleware`, or, when the application registered none, a middleware with the
defaults. The defaults are safe, so placing them is not a misconfiguration: no global limit and no named
policy, which leaves only endpoints whose `RateLimitingMetadata` carries an inline policy limited, and a
request for an endpoint that names a policy fails with `InvalidOperationException` rather than running
unlimited. That is what `UseRateLimiting()` without a callback always meant, and the route-convention
verbs' inline policies rely on it.

*Rejected:* the former `UseRateLimiting(Action<RateLimitingOptions>? configure)`; a configuration callback
on the pipeline verb split the feature's settings across two call sites and kept them out of the
registrations the host enumerates.

## Ordering

`UseForwardedHeaders` → `UseRouting` → `UseRateLimiting` → … → endpoint.

- **After `UseForwardedHeaders`**, so `RateLimitPartitionKeys.ClientAddress` keys on the effective client.
- **After `UseRouting`**, so the endpoint and its `RateLimitingMetadata` are published when the middleware
  runs. Placed ahead of `UseRouting`, the global limiter still applies to every request, but an
  endpoint whose metadata names a policy fails at dispatch (see "Fail closed" above).

## Lifetime and disposal posture

Limiters are built **once**, when the application registers rate limiting, and live for the **application
lifetime**. A `PartitionedRateLimiter<T>` is `IAsyncDisposable`. The middleware `AddRateLimiting`
registers is a container-owned singleton, which the application's service provider disposes at shutdown,
but the middleware is not disposable and **never disposes its limiters**: a policy is the application's
object, which it may share (between the global and a named slot, across two registrations, or as an
inline endpoint policy the route table holds), so disposing it would first need a rule for who owns a
shared limiter. The accepted posture therefore stays **process-lifetime**: the limiters are reclaimed at
process exit, along with the internal replenishment timers the window and token-bucket limiters run.
This is recorded honestly rather than hidden; disposing them at shutdown is a recorded follow-up.
**Per-request leases** are a separate concern and *are* released deterministically: each acquired lease
is held on the feature and disposed when the request completes (the middleware's `finally`), which is
the lifetime a concurrency limiter's permit requires.

## Rejection handling

On rejection the middleware sets the configured status (`RejectionStatusCode`, `429 Too Many Requests` by
default — RFC 6585 §4 / RFC 9110 §15.5.30) and, when the lease published `MetadataName.RetryAfter`, a
`Retry-After` header (delta-seconds via `HttpHeaderKey.RetryAfter`). The window and token-bucket limiters
publish that hint; the concurrency limiter does not, so it is genuinely optional. The `OnRejected` hook is
invoked **after** the status and header are set, so it observes and may override them or write a body; the
default is **bodyless**, which composes with the status-code-pages middleware (#881) that can upgrade the
bare 429. A rejection on an **already-committed response head** (detected via the response-streaming
feature) cannot rewrite the status, so the exchange is aborted at the protocol layer instead — the same
defensive path `Web.RequestTimeouts` takes. In normal use this never trips, because both gates run before
`next`; only a middleware placed ahead of rate limiting could have committed the head.

## Diagnostics

The internal `RateLimitingEventSource` uses the assembly name
`Assimalign.Cohesion.Web.RateLimiting`. It closes the operator-diagnostics follow-up from #783 without a
logging or hosting dependency. Enable it by name with `dotnet-trace` or `dotnet-counters`.

| Id | Event | Level | Payload |
|---|---|---|---|
| 1 | `RequestRejected` | Verbose | `gate`, `policyName`, `retryAfterSeconds:double`, `routeTemplate`, `requestId` |
| 2 | `RejectionAborted` | Warning | `gate`, `policyName`, `requestId`, `suppressedCount:int` |

`gate` is `global` or `endpoint`, including for an inline endpoint policy with no name. An unnamed policy
has an empty `policyName`; an absent retry hint is `-1`. The request id comes from
`IWebRequestIdFeature`, never ambient activity. The configured route template and request id are read only
inside `IsEnabled`. No partition key, request path, header value, identity, or response body is written.
`RejectionAborted` includes `requestId` in addition to the scope's original event table, satisfying
#1404's per-request correlation requirement; it fires before cancelling an already-committed response.
It is throttled to one event per second across the provider, and the next report carries the number
suppressed since the previous report (saturating at `int.MaxValue`).

The lazily published `rejected-requests` `PollingCounter` is an exact total. The single failed-acquisition
branch increments it once, before the application's observation or rejection hooks, even when no listener
is attached or the abort warning is suppressed. There is no admission counter or per-request success
event. Diagnostics are additive against `10.0.0-preview.1`. Under program decision 15, the new
entries are a diagnostic behavior break for applications forwarding the Web provider prefix into
configured sinks; no public API or rejection status/body behavior changes.

`RateLimitingOptions.OnDecision` remains the dependency-free application hook, invoked with an immutable
`RateLimitingDecision` (policy name, admitted/rejected, retry-after) for each limiter decision.
OpenTelemetry instruments remain #317's follow-up. "Queued" is not surfaced separately: the BCL does not
expose a per-lease queued signal cleanly.

## AOT posture

Options resolve to prebuilt limiters and captured delegates at registration; request-time work is an
`AcquireAsync` per gate, a metadata read, and a header set. No reflection, no configuration
binding, no service location, no runtime code generation. The partition-key selectors are plain delegates.
The BCL engine is AOT-safe.
The source uses primitive event payloads without reflection; a NativeAOT consumer enables event delivery
with `EventSourceSupport=true`. Limiter behavior and exact rejection accounting do not depend on delivery.

## Non-goals

- **No limiter algorithm.** Cohesion never reimplements a token bucket or window; the BCL engine is the
  only algorithm source.
- **No hosting integration.** The package must not (and cannot, per `COHRES001`) reference `Web.Hosting`.
  Pipeline placement is the application's registration-order responsibility (routing's fail-closed check
  catches a misplaced middleware for endpoints with a policy), and OTel/metrics wiring is a
  hosting-composition follow-up.
- **No client-side limiter.** Outbound/execution-side rate limiting is `Resilience.RateLimiting` under
  epic #318; graduating it to the `UseRateLimiter` builder-extension model is explicitly out of scope here.
- **No replace-the-global endpoint model.** Endpoint policies are additive to the global limiter (see
  above).

## Scope-creep candidates (recorded, not taken)

- Disposing the limiters at application shutdown instead of relying on process-lifetime reclamation. The
  hook exists now, because the container disposes the middleware `AddRateLimiting` registers; what is
  missing is an ownership rule, so a limiter the application shares is never disposed underneath it.
- A hosting-composition seam that wires `OnDecision` to Cohesion OpenTelemetry/metrics without a
  feature-package → hosting reference.
- Surfacing the BCL "queued" transition to `OnDecision` if a clean per-lease signal becomes available.

## Testing

`tests/RateLimitingMiddlewareTests.cs` builds the middleware through `RateLimitingBuilder` and drives it over
a composing pipeline builder and an `IHttpContext` double (`tests/TestObjects/`); a stage ahead of the
middleware publishes a fake route match, which is what `UseRouting` does. The same pipeline builder's
application context lists registered middleware, so the `UseRateLimiting` tests cover placement: the
registered middleware, the last of two registrations, the defaults without one, and the `Build` snapshot.
`tests/RateLimitingEndToEndTests.cs` drives it over the in-memory `WebApplicationTestFactory` with the real
router, registering through the generated `AddRateLimiting` verb (both overloads). Determinism comes from a
**fixed-window, one-permit** policy: a window limiter does not return its permit on lease disposal, so a
second same-window request is rejected with **no timing dependency** (the window is an hour, far beyond any
test). The **concurrency** limiter (permit returned on completion) covers the "permit held for the request
lifetime" semantic through two genuinely concurrent unit-level executions, and, with a one-request queue,
the queueing endpoint gate: the test waits on the limiter's `CurrentQueuedCount` rather than a delay, so it
knows the second request is parked before releasing the first. The in-memory driver's default HTTP/1.1
connection dispatches one exchange at a time, so an intra-connection concurrency test over it would
deadlock; HTTP/2 streams are dispatched concurrently (#1049), so an end-to-end concurrency test needs the
factory's HTTP/2 protocol. Coverage: admit/reject, the 429 + `Retry-After` answer, a custom rejection
status, the `OnRejected` and `OnDecision` hooks, forwarded-composing client-address partitioning, named /
inline / disabled / unknown per-endpoint policies, the global-then-endpoint decision order, queueing and
cancellation while queued on the endpoint gate, the CORS-preflight skip (unit and through the real router),
the committed-head abort, the concurrency permit hold, and registration ahead of `UseRouting` (the global
limiter still gates; an endpoint with a policy fails at dispatch).

`RateLimitingEventSourceTests` runs in a nonparallel collection and links the canonical test-only
`EventSourceRecorder`. It checks the provider name, strict manifest, real global/named/inline rejections,
request-feature correlation despite an unrelated ambient activity, payload privacy, both retry-hint
shapes, throttled committed-head aborts with exact counts, disabled-listener lazy payload reads, and
counter publication. Event assertions use request ids and counters use deltas.

## Exception messages (#1467, #1471)

Resource names are short PascalCase noun phrases naming the failure, at most 40 characters.
The internal naming cleanup preserves message text, placeholders and resource order.

Exception text lives in `src/Properties/ErrorMessages.resx`. The shared build targets generate
an internal `ErrorMessages` accessor in this project's root namespace under the intermediate
output directory; no designer is checked in. Formatted messages use
`string.Format(CultureInfo.CurrentCulture, ErrorMessages.<Name>, ...)`. Resource extraction
preserves the existing exception text and input-disclosure rules, including any diagnostic
type-only exclusions. EventSource templates, log messages and response bodies retain their
existing ownership. This is a conventions change with no public API or behavior break against
10.0.0-preview.1.
