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

The package owns: two options-carried hooks, one options object, one sealed policy type, one sealed
metadata carrier, a static partition-key helper, a typed feature, and one `extension(IWebApplicationPipelineBuilder)`
verb. Everything request-time is an `AcquireAsync`/`AttemptAcquire` against a prebuilt limiter and a
status write.

## The policy model — a global limiter plus named policies, additive

`RateLimitingOptions` carries a `GlobalPolicy` and a set of named policies (`AddPolicy`). A
`RateLimitingPolicy` is a `PartitionedRateLimiter<IHttpContext>` plus a permit count — built once, at
builder time.

- **The global limiter is the must-have.** It is applied to **every** request and acquired **up-front**,
  before any downstream work, with the limiter's **full queueing semantics** (`AcquireAsync`). It is the
  primary flood shield.
- **Named policies attach per-endpoint** through `RateLimitingMetadata` in the routing metadata bag. A
  per-endpoint policy is evaluated **in addition to** the global limiter — **both must grant a lease**.

### Why additive, not replace

`Web.RequestTimeouts` (the sibling per-endpoint-metadata feature) has an endpoint policy **replace** the
global default. Rate limiting deliberately does **not**, because the two features acquire differently in
time. A timeout arms a timer that can be re-armed mid-flight; a rate limiter **spends a permit** at
acquisition. The global lease is acquired **before routing identifies the endpoint** (see the router
constraint below), so it cannot be retroactively skipped — a "replace" model would already have consumed
a global permit by the time the endpoint policy was known. The additive model is also faithful to ASP.NET
Core's `GlobalLimiter`, which is likewise combined with (not replaced by) endpoint limiters.
`RateLimitingMetadata.Disabled` removes only the **per-endpoint** gate; the global limiter still applies.

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

## Per-endpoint mechanics — the single-middleware router constraint

Cohesion's router **matches and dispatches in one middleware** (`RouteAsync`/`UseRouting` calls
`SetRouteMatch` then invokes the handler in the same call). There is no pipeline position "between match
and handler" for a separate middleware to gate from — the same constraint `Web.RequestTimeouts` documents.
The seam is the router's **route-match publication**: it installs `IRouteMatchFeature` on
`IHttpContext.Features` before running the handler.

So the middleware hands downstream a **decorated context** whose feature collection
(`RateLimitingFeatureCollection`) observes that publication. When the matched endpoint carries
`RateLimitingMetadata`, the decorator resolves the policy and gates the request **at that moment, before
the handler runs** — exactly mirroring the `Web.RequestTimeouts` decorator that re-arms its timer there.

Two consequences follow from the seam being **synchronous** (`Set` is `void`):

1. **The per-endpoint acquire is synchronous and non-queueing** (`AttemptAcquire`). Queueing (an
   `await`) cannot happen in a synchronous `Set`, so it stays a global-limiter concern; an endpoint policy
   admits or rejects immediately. This is an honest limitation of the single-middleware router, not a
   design preference.
2. **A per-endpoint rejection is raised as an internal signal** (`RateLimiterRejectedSignal`) from the
   synchronous publication, carrying the rejected lease. The middleware catches it — the signal never
   escapes to an outer exception boundary — and writes the rejection **before the handler runs**. This is
   the same "catch and translate at the outer middleware" shape `Web.RequestTimeouts` uses for its
   expiry `OperationCanceledException`. Because it is control flow via an exception, it is scoped to the
   lower-volume per-endpoint path; the high-volume flood path is the global limiter, which short-circuits
   cleanly (no throw).

The endpoint gate applies **at most once per exchange** (a re-published match does not acquire a second
lease).

**Ordering constraint that follows:** the signal travels the pipeline segment between the router and
`UseRateLimiting`, so no catch-all middleware may sit between them — in particular, the `Web.ErrorHandling`
exception boundary belongs **outside** `UseRateLimiting` (its usual outermost position), never between
`UseRateLimiting` and `UseRouting`, or it would swallow the signal and render a rejection as a 500.

## Queueing semantics

`QueueLimit`/`QueueProcessingOrder` pass through the BCL limiter options unchanged — the package configures
nothing about queueing itself. The global limiter, acquired with `AcquireAsync`, **honors queueing fully**:
a request waits for a permit up to the queue limit. The per-endpoint gate uses `AttemptAcquire` and so does
not queue, per the router constraint above.

## Lifetime and disposal posture

Limiters are built **once**, at builder time, and live for the **application lifetime**. A
`PartitionedRateLimiter<T>` is `IAsyncDisposable`, but the Web pipeline exposes **no disposal hook** a
feature package can attach to — so the accepted posture is **process-lifetime**: the middleware holds the
limiters and they are reclaimed at process exit, along with the internal replenishment timers the window
and token-bucket limiters run. This is recorded honestly rather than hidden; a pipeline-level disposal
hook is a recorded follow-up. **Per-request leases** are a separate concern and *are* released
deterministically: each acquired lease is held on the feature and disposed when the request completes
(the middleware's `finally`), which is the lifetime a concurrency limiter's permit requires.

## Rejection handling

On rejection the middleware sets the configured status (`RejectionStatusCode`, `429 Too Many Requests` by
default — RFC 6585 §4 / RFC 9110 §15.5.30) and, when the lease published `MetadataName.RetryAfter`, a
`Retry-After` header (delta-seconds via `HttpHeaderKey.RetryAfter`). The window and token-bucket limiters
publish that hint; the concurrency limiter does not, so it is genuinely optional. The `OnRejected` hook is
invoked **after** the status and header are set, so it observes and may override them or write a body; the
default is **bodyless**, which composes with the status-code-pages middleware (#881) that can upgrade the
bare 429. A rejection on an **already-committed response head** (detected via the response-streaming
feature) cannot rewrite the status, so the exchange is aborted at the protocol layer instead — the same
defensive path `Web.RequestTimeouts` takes. In normal use this never trips, because both gates precede the
handler.

## Telemetry — the observation hook, not OpenTelemetry

The issue asked for lease acquired/rejected/queued counters wired through Cohesion OpenTelemetry/Logging at
composition time. That is **re-scoped**: a feature package cannot reach a hosting-layer OTel/Logging seam
without referencing `Web.Hosting`, which `COHRES001` forbids. Instead the package offers a **lightweight,
dependency-free observation hook** — `RateLimitingOptions.OnDecision`, invoked with an immutable
`RateLimitingDecision` (policy name, admitted/rejected, retry-after) for each limiter decision. Wiring that
hook to a Cohesion OTel/metrics seam at hosting-composition time is a recorded follow-up candidate. "Queued"
is not surfaced separately: the BCL does not expose a per-lease queued signal cleanly, and the
admitted/rejected decision is what the hook reports.

## AOT posture

Options resolve to prebuilt limiters and captured delegates at registration; request-time work is an
`AcquireAsync`/`AttemptAcquire`, a metadata read, and a header set. No reflection, no configuration
binding, no service location, no runtime code generation. The partition-key selectors are plain delegates.
The BCL engine is AOT-safe.

## Non-goals

- **No limiter algorithm.** Cohesion never reimplements a token bucket or window; the BCL engine is the
  only algorithm source.
- **No hosting integration.** The package must not (and cannot, per `COHRES001`) reference `Web.Hosting`.
  Pipeline placement is the application's registration-order responsibility, and OTel/metrics wiring is a
  hosting-composition follow-up.
- **No client-side limiter.** Outbound/execution-side rate limiting is `Resilience.RateLimiting` under
  epic #318; graduating it to the `UseRateLimiter` builder-extension model is explicitly out of scope here.
- **No queueing on the per-endpoint gate.** The synchronous route-match seam cannot await; queueing is a
  global-limiter capability.
- **No replace-the-global endpoint model.** Endpoint policies are additive to the global limiter (see
  above).

## Scope-creep candidates (recorded, not taken)

- A pipeline-level disposal hook so the middleware can dispose its limiters at application shutdown instead
  of relying on process-lifetime reclamation.
- A hosting-composition seam that wires `OnDecision` to Cohesion OpenTelemetry/metrics without a
  feature-package → hosting reference.
- Surfacing the BCL "queued" transition to `OnDecision` if a clean per-lease signal becomes available.

## Testing

`tests/RateLimitingMiddlewareTests.cs` drives the middleware through its public verb over a capturing
pipeline builder and an `IHttpContext` double (`tests/TestObjects/`), and
`tests/RateLimitingEndToEndTests.cs` drives it over the in-memory `WebApplicationTestFactory`. Determinism
comes from a **fixed-window, one-permit** policy: a window limiter does not return its permit on lease
disposal, so a second same-window request is rejected with **no timing dependency** (the window is an hour,
far beyond any test). The **concurrency** limiter (permit returned on completion) covers the
"permit held for the request lifetime" semantic through two genuinely concurrent unit-level executions —
never over the in-memory driver, whose per-connection dispatch is sequential and would deadlock an
intra-connection concurrency test. Coverage: admit/reject, the 429 + `Retry-After` answer, a custom
rejection status, the `OnRejected` and `OnDecision` hooks, forwarded-composing client-address partitioning,
named / inline / disabled / unknown per-endpoint policies, the committed-head abort, and the concurrency
permit hold.
