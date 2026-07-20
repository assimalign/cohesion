# Assimalign.Cohesion.Web.Caching — Design

Server-owned output caching for the Cohesion Web pipeline. This document records the reasoning behind the
middleware's shape so a future reader does not re-derive it from the code.

## Design intent

Cohesion already owns a cache foundation (`Assimalign.Cohesion.Caching` + `.InMemory`, with size limits,
priorities, and token invalidation) and the RFC 9111 typed primitives (`HttpCacheControl`,
`HttpFreshness`, the method safety/cacheability traits) from #755. Output caching is therefore a thin
**middleware + policy** layer over those, not a storage stack: it decides *whether* to store or serve and
*how the key is built*, and delegates *storage* to an async, tag-aware store seam whose default adapts the
synchronous `MemoryCache`.

The package is a dependency-free feature library composed against the Web root. It never references
`Web.Hosting` (the resource hosting-isolation rule, `COHRES001`); options and the store are captured at
builder time and no request-time service location occurs.

## Endpoint resolution — pre-flight match, not the reactive decorator (why-this-not-that)

Per-endpoint policy lives in a sealed `OutputCacheMetadata` carrier on the route, resolved last-wins with
`IRouterRouteMetadataCollection.GetMetadata<OutputCacheMetadata>()` — the same endpoint-metadata seam
`Web.RateLimiting` and `Web.RequestTimeouts` read. The **mechanism** for reaching it differs, and
deliberately so.

`Web.RateLimiting` observes the router publishing its match (`IRouteMatchFeature`) through a feature-collection
decorator and acts **synchronously** at that seam. Output caching cannot: Cohesion's router matches **and
dispatches** in one step (`RouteAsync` publishes the match, then immediately invokes the handler), so there
is no pipeline slot between "matched" and "handler runs" for an **async** concern that must **skip the
handler on a hit**. The reactive seam is synchronous, and a store lookup is asynchronous; and even if the
lookup blocked, a *miss* discovered at that seam could no longer let the handler run (it was about to be
invoked).

So the middleware runs **ahead of `UseRouting`** and performs the router's own **side-effect-free**
`IRouter.Match(context)` itself (reachable via the per-application `IRouterFeature`) to discover the
endpoint and read its metadata *before* deciding. The match is a pure function over an immutable route
table, so this pre-flight is deterministic and identical to the match `UseRouting` computes moments later —
it publishes nothing and dispatches nothing. When routing is not registered the base policy alone governs.
This is the async-correct adaptation of the shipped metadata precedent, reading the same carrier at the
same seam, resolved proactively rather than reactively.

## Policy model

- **`OutputCachePolicy`** — freshness `Duration` (the time-to-live), the `VaryBy*` key dimensions,
  `CacheAuthenticated`, `HonorResponseCacheControl`, an optional per-policy `MaximumBodySize`, and `Tags`.
- **`OutputCacheOptions`** — the `BasePolicy`, a named-policy registry (`AddPolicy`, ordinal names, mirroring
  `RateLimitingOptions`), the middleware-wide `MaximumBodySize` and store `SizeLimit`, and the `TimeProvider`.
- **`OutputCacheMetadata`** — the sealed per-endpoint carrier: a named policy, an inline policy,
  `Enabled` (opt in under the base/default policy), or `Disabled` (opt out).

**Resolution** (in the middleware): a matched endpoint's metadata wins last-wins over the base policy —
inline policy → named policy → `Enabled` (base/default) → `Disabled` (no caching). With no metadata the
`BasePolicy` governs; with neither a base policy nor endpoint metadata the request is not cached
(**opt-in mode**). A `null` or disabled resolved policy is a clean passthrough.

## Cache key — primary key plus a Vary variant

The **primary key** is built before the endpoint runs from the request method, scheme, host, and path,
plus the policy's `VaryBy*` rules: `VaryByHeaders` (request-header values), `VaryByRouteValues` (matched
route values from the pre-flight match), and `VaryByQueryKeys` (empty folds the *entire*, sorted query
string; non-empty selects listed keys). Components are fenced with the ASCII unit separator so boundaries
are unambiguous without hashing; a distributed adapter may hash the string.

The **variant key** folds in the stored response's own `Vary` header (see below).

## THE VARY DECISION — honor the response `Vary` header (RFC-faithful), with ordering

This branch also ships `Web.Compression` (response varies by `Accept-Encoding`) and #149 content
negotiation (varies by `Accept`); both **stamp the response's own `Vary` header** and append to any
existing one. The output cache therefore chooses option **(a) — automatically honor the response's own
`Vary` header in the cache key (RFC 9111 §4.1)** — rather than ASP.NET-style policy-only `VaryBy*`.

Why (a) and not (b): policy-only varying is fragile — if the operator forgets to declare
`VaryByHeader("Accept-Encoding")` while compression is active, a `br` variant and a `gzip` client collide
on one key and the cache serves brotli to a client that cannot decode it. Reading the response's actual
`Vary` makes that **impossible by construction**, regardless of policy configuration, and composes exactly
with what compression/negotiation already emit. Policy `VaryBy*` still exists as an **additive** refinement
on the primary key.

### How it is stored and looked up (the marker indirection)

The response `Vary` is not known until the endpoint has run, so a first lookup cannot compute the variant
key. The store therefore holds two entry shapes under one primary key:

- **No `Vary`** → the representation is stored **directly** under the primary key (one-lookup hit path).
- **`Vary` present** → a lightweight **vary marker** (`Body == null`, carrying the `Vary` field-names) is
  stored under the primary key, and the representation under `primaryKey + variant-suffix`, where the
  suffix is the current request's values of those field-names.

A lookup reads the primary key; a direct representation is served immediately, a marker drives a second
read of the variant the current request maps to. A **`Vary: *`** response is treated as uncacheable and not
stored. If a later response for the same primary key carries a *different* `Vary`, it overwrites the marker;
older variants become unreachable and expire by time-to-live — conservative (worst case a miss, never a
wrong-variant serve).

### Ordering (load-bearing)

Register `UseOutputCache` **outside** (before) `UseResponseCompression` and any content-negotiated write.
The buffered tee then captures the **fully-encoded** bytes, and the captured `Vary` already carries
`Accept-Encoding`/`Accept`. On a hit the compression/negotiation middleware (inner) never runs; the cache
replays the stored encoded bytes with their `Content-Encoding`, and because the variant key folds in the
client's `Accept-Encoding`, a `gzip`-only client computes a different key than the stored `br` variant and
misses rather than mis-decoding. Registering it *inside* compression would cache the pre-compression bytes
against a `Vary` compression is about to stamp — the mis-serve this design exists to prevent. A test
(`UseOutputCache_ResponseVary_ShouldNotServeForeignVariant`) proves the cross-client case over a generic
`Vary` header.

## Bypass matrix

**Request-side** (decided before the endpoint runs — the request is not served/stored):

| Condition | Behavior |
| --- | --- |
| Method not `GET`/`HEAD` | passthrough (no caching) |
| No applicable/enabled policy | passthrough |
| Endpoint metadata `Disabled` | passthrough |
| Request `Cache-Control: no-store` or `no-cache` | passthrough (conservative) |
| `Authorization` header present, policy `CacheAuthenticated == false` | passthrough |

**Response-side** (decided after the endpoint runs — the response is not stored):

| Condition | Behavior |
| --- | --- |
| Status ≠ `200 OK` | not stored (conservative — see below) |
| Response `Set-Cookie` present, policy `CacheAuthenticated == false` | not stored |
| Response `Cache-Control: no-store` / `private` / `no-cache` (when `HonorResponseCacheControl`) | not stored |
| `Vary: *` | not stored |
| Body exceeds the per-entry cap | not stored (streamed through untouched) |
| Effective time-to-live ≤ 0 | not stored |
| Entry larger than the store's whole `SizeLimit` | declined by the store |

**Why only `200`:** the conservative default caches exactly `200 OK`. Other 2xx (`204`/`206`) and the
"heuristically cacheable" statuses (`203`, `300`, `301`, `308`, `404`, `410`, …) are deliberately excluded —
they invite subtle correctness bugs (partial content, absent bodies, negative caching) that a first output
cache should not take on. Widening the set is an additive future change.

**Authenticated responses** are never cached by default: an `Authorization` request or a `Set-Cookie`
response both bypass unless the policy sets `CacheAuthenticated`. That opt-in is dangerous (it shares a
per-user representation) and is intended only alongside a `VaryBy*` that segments the key per principal —
the caller owns that correctness. The opt-in covers the *response*, never the cookie grant itself:
`Set-Cookie` is in the non-cacheable header set unconditionally, so a stored entry can never replay one
client's cookie (say, a freshly minted session id) to another. A hit therefore carries the shared body
and headers but no `Set-Cookie`.

## Time-to-live and Age

The effective time-to-live is the policy `Duration`. When `HonorResponseCacheControl` is set (default) and
the response declares an explicit freshness lifetime (`Cache-Control: s-maxage`/`max-age`, or
`Expires` − `Date`, via `HttpFreshness.GetFreshnessLifetime` as a **shared** cache), the smaller of the two
wins — the origin may **shorten** but never **lengthen** the policy cap; a `max-age=0` response is not
stored. Client revalidation semantics (conditional requests, `304`, `stale-while-revalidate`) are out of
scope (they belong to #755's client story); the cache serves only genuinely fresh entries and lets an entry
lapse at its time-to-live.

On a hit the served `Age` is `now − CreatedAt` in whole seconds, computed on the same `TimeProvider` the
store expires against (they are threaded together so the clocks agree, and tests can drive a manual clock).

## Store seam

`IOutputCacheStore` is the async, tag-aware seam: `GetAsync(key)`, `SetAsync(key, entry)`,
`EvictByTagAsync(tag)`. It layers **above** the synchronous `Assimalign.Cohesion.Caching` foundation, per
that library's DESIGN rule that distributed/async surfaces belong in the consumer. Entries
(`OutputCacheEntry`) are opaque to the store beyond their size, time-to-live, and tags — the response
framing (status, headers as `OutputCacheHeader` carriers, body bytes, vary marker) is the middleware's
concern, so any store that returns the exact entry round-trips a response losslessly. The entry is a plain
data object (value types, strings, one `byte[]`), so the in-memory store holds it **with no serialization**
(fully AOT-clean) and a distributed adapter can frame it without reflection.

### Default in-memory store and size accounting

`InMemoryOutputCacheStore` (public, so an application can construct and hold it for tag eviction) adapts
`MemoryCache`:

- **Size accounting.** The store sets `MemoryCacheOptions.SizeLimit` and declares each entry's `Size`
  (`OutputCacheEntry.Size` = body bytes + captured header text + a floor). The foundation enforces the
  cumulative limit with its priority/LRU capacity eviction; an entry larger than the whole limit is
  declined and the store drops it silently rather than surfacing a fault to the request path. This is the
  **total** cap; the **per-entry** cap (`OutputCacheOptions.MaximumBodySize`) is enforced earlier by the
  buffering middleware.
- **Time-to-live.** Entries use absolute expiration (`AbsoluteExpirationRelativeToNow`), not a sliding
  window, so a response is served only while genuinely fresh.
- **Tag index.** A separate `tag → keys` index backs `EvictByTagAsync`; a post-eviction callback prunes a
  key from its tags on any eviction (expiry, capacity, replacement, removal), so the index self-cleans.
  Tags are re-indexed *after* the commit so a replacement's own replaced-eviction cannot un-index the new
  entry.

## Buffering — the tee, and the no-clobber discipline

On a miss the middleware replaces the settable `IHttpResponse.Body` with an `OutputCacheBufferStream`: a
**tee** that writes every byte **straight through** to the transport's real body first (the client is always
served) while capturing a copy up to the per-entry cap. Crossing the cap **abandons the capture** — the
buffer is released and writes continue to flow through untouched, so an over-large response streams normally
and is simply not cached. This mirrors the `Web.Compression` `CompressionBodyStream` discipline: caching is
a strictly additive side effect that can never corrupt or withhold the response. The original body is always
restored in a `finally`, and storage runs only after the endpoint returns normally (an exception skips it).

## QUERY posture (RFC 10008 §2.7) — GET/HEAD only, QUERY a documented follow-up

The cacheable set is **`GET` and `HEAD`**. QUERY is deliberately excluded despite
`HttpMethod.Query.IsCacheable` and `CacheKeyIncludesContent` reporting `true`: RFC 10008 §2.7 requires the
request **content** to be part of the cache key, and Cohesion exposes **no request-content key seam** that is
both available before dispatch and non-destructive. `IHttpRequest.Body` is a forward-only stream on some
transports (the shipped `Web.Query` validation middleware itself never reads it, peeking only length
metadata); reading and hashing it to key on content would consume the body and break the endpoint. Rather
than half-implement QUERY caching with a transport-dependent, body-consuming key, it is a recorded
follow-up: **when a buffered/rewindable request body or a request content-digest seam lands, QUERY joins the
cacheable set keyed by (target + content-digest)** — the `CacheKeyIncludesContent` trait is already the flag
for that path.

## Tag eviction reachable from application code

Two routes, both over the same store:

- The application constructs an `InMemoryOutputCacheStore` (or any `IOutputCacheStore`), passes it to the
  `UseOutputCache(store, …)` overload, and calls `store.EvictByTagAsync(tag)` directly.
- A handler resolves `IOutputCacheFeature` from `context.Features` and calls its `EvictByTagAsync` — a thin,
  stateless handle over the same store, installed on every exchange.

An `IChangeToken` bridge (mapping a tag to a foundation change token) was considered and **not taken** — the
explicit `EvictByTagAsync` surface is simpler and sufficient; a token bridge can layer additively later.

## AOT posture

No reflection, no dynamic serialization, no runtime code generation. The stored entry is plain data held
directly by the in-memory store; keys are built with a `StringBuilder`; the pre-flight match and metadata
resolution are the router's own reflection-free `is`-test seam. Registration is dependency-free — options
and the store are captured at builder time.

## Non-goals

- **Distributed store backends.** This package ships the middleware over the store *seam* with the in-memory
  default; a distributed cache / key-value / database store is a follow-up adapter implementing
  `IOutputCacheStore` (the future Data Platform cache service, #57/#58/#60).
- **Client revalidation semantics.** Conditional requests, `304 Not Modified`, `stale-while-revalidate`, and
  `stale-if-error` are out of scope (tracked by #755's client story). The cache serves only fresh entries.
- **QUERY caching** until a request-content key seam exists (see the QUERY posture above).
- **Caching non-`200` statuses** by default (conservative; additive to widen).
- **Owning DI/config/logging.** Feature packages never take the hosting module (`COHRES001`); the store is
  supplied to `UseOutputCache`, not resolved from a container.

## Scope-creep candidates (recorded, not taken)

- **Distributed `IOutputCacheStore` adapters** over `Database.KeyValuePair` (has etag/CAS) or a
  `libraries/Cache` distributed contract when it matures.
- **An `IChangeToken` → tag bridge** for configuration-driven purges.
- **Widening the cacheable status set** (`301`/`308`/`404`/`410` negative caching, `206` range caching).
- **QUERY caching** once a buffered/rewindable request body or request content-digest seam lands.
- **Conditional-request generation** (emit `ETag`/`Last-Modified` and answer `If-None-Match` from cache)
  once the client-revalidation story from #755 is picked up.

## Testing

Unit tests cover the in-memory store (round-trip, miss, absolute time-to-live over a manual clock, tag
eviction and the re-tag safety, oversized decline), the key builder (query order-independence, `VaryBy`
partitioning, the response-`Vary` variant partition), and the middleware bypass matrix over an in-memory
context double (hit skips downstream + stamps `Age`, authenticated/`Set-Cookie`/`no-store`/non-200/over-cap
bypass, non-cacheable method). End-to-end tests over `WebApplicationTestFactory` (in-memory HTTP/1.1) prove
a hit skips the endpoint (downstream-invocation counting), a differing query misses, the response `Vary`
keeps a client from a foreign variant, an authenticated request bypasses, tag eviction forces a re-fetch,
and per-endpoint opt-in through routing metadata caches only the marked endpoint.
