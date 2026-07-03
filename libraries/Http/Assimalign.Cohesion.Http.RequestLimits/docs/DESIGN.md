# Assimalign.Cohesion.Http.RequestLimits — Design

> **Status: scaffold.** The package and the core interception seam it consumes are scaffolded
> and unit-tested, but the server transport does not invoke interceptors yet and
> `IHttpMaxRequestBodySizeFeature` still lives in core `Assimalign.Cohesion.Http`. The
> "Migration plan" section below is the implementation contract for finishing the move; this
> banner is deleted when that lands.

Per-request limit features for the Cohesion HTTP server, starting with the typed
max-request-body-size feature. The package owns the feature contract and its implementation and
references only core `Assimalign.Cohesion.Http` — the transport never references this package.

## Why this package exists (seams vs. features)

The max-request-body-size feature originally shipped inside core `Assimalign.Cohesion.Http`
(contract) and `Assimalign.Cohesion.Http.Connections` (implementation, seeded by the h1 parser).
That placement conflated two different kinds of surface:

- **Seams** — generic extensibility infrastructure with no opinion about any one capability:
  `IHttpFeature`, `IHttpFeatureCollection`, and now `IHttpRequestInterceptor` +
  `HttpRequestInterceptorContext`. These belong in core, exactly like the shared wire rules in
  `HttpFieldNormalization`.
- **Features** — concrete capabilities: extended CONNECT, sessions, cookies, forms, and
  per-request body-size limiting. These belong in their own packages that reference only core.

The repo already encodes this taxonomy: `IHttpExtendedConnectFeature` lives in
`Assimalign.Cohesion.Http.ExtendedConnect`, not in core, and the transport stays decoupled from
it. This package restores that discipline for the body-size feature. The enforcement itself —
the wire-level cap with 413 semantics — is *not* a feature and stays transport-owned in
`Http.Connections` (`HttpServerLimits.MaxRequestBodySize`): the security guarantee must hold
with zero optional packages installed.

## How it works

`HttpRequestLimits.CreateMaxRequestBodySizeInterceptor()` returns a stateless
`IHttpRequestInterceptor` the composition root registers on the server's listener options. Per
request, its head hook attaches an internal `HttpMaxRequestBodySizeFeature` to the exchange's
feature collection.

The feature is a **write-through view over the parse context**, not a copy:

- `MaxRequestBodySize` reads and writes `HttpRequestInterceptorContext.MaxRequestBodySize` —
  the very value the transport enforces. There is no second source of truth.
- `IsReadOnly` delegates to the context's transport-owned freeze flag
  (`IsMaxRequestBodySizeReadOnly`). The transport freezes the knob when it starts consuming the
  body — at body materialization under today's buffered read, at the first body byte once the
  streaming-body rework lands. Because the feature holds no frozen copy of its own, that timing
  change is invisible to this package and to feature consumers: the contract ("adjust before the
  body is read; observe any time") is stable while the transport's definition of "read" evolves.

The transport keeps the context alive until the request body is consumed (documented on
`HttpRequestInterceptorContext`), so the view never dangles.

### Why a typed seam here, when ExtendedConnect chose an Items-key bridge

ExtendedConnect deliberately bridges transport → package with a convention-named
`IHttpContext.Items` string and **no shared symbol**. That is the right shape for its job:
one-way publication of an immutable value after parse. This package needs three things the
Items bridge cannot express:

1. **Mutation with enforcement coupling** — the feature must write a value the transport then
   enforces mid-parse, not merely read one it published.
2. **Pre-dispatch attachment** — the feature must exist on `Features` before the first
   middleware runs, without the transport knowing the feature type.
3. **Stream wrapping** — returning a replacement body stream has no Items-key analogue.

Those three are exactly the `IHttpRequestInterceptor` surface, which is why the escalation to a
compile-time shared seam (in core, shared by all future parse-time features) is justified. New
capabilities that only need one-way post-parse publication should still prefer the Items-key
bridge.

## Ordering and defaults

Register this package's interceptor **first**. Its head hook attaches the feature; later
interceptors' head hooks may then look it up (or simply write the context knob — same store).
The web host (`Assimalign.Cohesion.Web.Hosting`) installs it by default so every request carries
the typed feature, preserving the behavior the transport shipped when it seeded the feature
itself; the raw transport remains lean (zero interceptors ⇒ no per-request context or feature
allocation).

## Feature identity

`Name` is `"Assimalign.Cohesion.Http.MaxRequestBodySize"` — kept byte-identical to the value the
transport historically used, because the string is the feature collection's dictionary key and
any name-keyed consumer would otherwise silently miss.

## Migration plan (implementation contract for the un-scaffolded half)

1. **Move the contract**: relocate `IHttpMaxRequestBodySizeFeature` from
   `Assimalign.Cohesion.Http/src/Abstractions/` into this package's `src/`. Both assemblies use
   the `Assimalign.Cohesion.Http` namespace (recorded csproj deviation), so the move is
   source-compatible for consumers; only project references change.
2. **Rewire the transport** (`Http.Connections`): delete its internal
   `HttpMaxRequestBodySizeFeature` and all seeding in `Http1MessageReader` (create/attach/
   freeze); add `HttpConnectionListenerOptions.Interceptors`, snapshotted to an array when the
   listener is constructed (post-start mutation must not race the accept loops); build the
   `HttpRequestInterceptorContext` per request (read-only `Headers` view via `AsReadOnly()`),
   run head hooks after the head parse, enforce `context.MaxRequestBodySize` (413), freeze the
   knob at body consumption, chain body hooks over the materialized stream, and flow the
   feature collection into the context through the already-present-but-unused `features`
   constructor parameters. Catch `HttpRequestRejectedException` ahead of the wire-failure
   classifier and answer via the minimal-status-response writer. Zero registered interceptors
   must keep the exact pre-seam fast path (no context, no feature, no hook dispatch).
3. **Clean the stale artifacts**: the `CreateFeatures` comment in `TransportHttpContext`
   (lines 27–34), the dangling `<see cref="HttpConnectionListenerOptions.CreateFeatures"/>` at
   its `DisposeAsync` doc, and the Connections `DESIGN.md` "Per-request feature injection"
   section — all describe a factory that never shipped; interceptors supersede it.
4. **Web.Hosting**: register `HttpRequestLimits.CreateMaxRequestBodySizeInterceptor()` first on
   the options by default.
5. **Tests**: transport-level tests move from "transport seeds the feature" to "interceptor
   attaches the feature"; keep the h1 limit-rejection suite intact (enforcement is unchanged).

## Protocol coverage (honest gaps)

- The interceptor seam and the cap enforcement are wired for **HTTP/1.1**. HTTP/2 and HTTP/3
  currently buffer a stream's body **before** decoding its head (h2: `Http2Stream.CreateContext`
  runs only when the stream is complete; h3: the request stream is drained before header
  decode), so on those protocols a head hook runs before the body is *exposed*, not before it is
  *received* — and no body-size cap is enforced there today at all. h2/h3 abuse limits are
  tracked separately (#764, #750); wiring the hook invocation into their context-construction
  sites is part of the implementation follow-ups. Nothing in this package's docs should be read
  as implying h2/h3 body protection exists yet.
- Data-rate limits and the middleware-visible pre-read override window depend on the
  streaming-body rework (#810).

## AOT posture

No reflection, no codegen. Feature lookup is the ordinal-string dictionary read of
`HttpFeatureCollection`; the interceptor is a plain interface dispatch; the feature is one small
allocation per request, only when the interceptor is registered.

## Non-goals

- **Enforcement.** The wire-level cap and its 413 semantics are transport-owned
  (`HttpServerLimits`); this package only observes and adjusts the per-request value.
- **Other limit knobs (request line, header count/size, timeouts).** Those are connection-wide
  policy on `HttpServerLimits` with no per-request story; they gain typed features here only if
  a real per-request consumer appears.
- **Client-side limits.** This is a server-side surface.
