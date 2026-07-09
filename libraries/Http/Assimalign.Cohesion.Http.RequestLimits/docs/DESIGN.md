# Assimalign.Cohesion.Http.RequestLimits — Design

Per-request limit features for the Cohesion HTTP server, starting with the typed
max-request-body-size feature. The package owns the feature contract and its implementation and
references only core `Assimalign.Cohesion.Http` — the transport never references this package.

## Why this package exists (seams vs. features)

The max-request-body-size feature originally shipped inside core `Assimalign.Cohesion.Http`
(contract) and `Assimalign.Cohesion.Http.Connections` (implementation, seeded by the h1 parser).
That placement conflated two different kinds of surface:

- **Seams** — generic extensibility infrastructure with no opinion about any one capability:
  `IHttpFeature`, `IHttpFeatureCollection`, and now `IHttpExchangeInterceptor` +
  `HttpExchangeInterceptorRequestContext`. These belong in core, exactly like the shared wire rules in
  `HttpFieldNormalization`.
- **Features** — concrete capabilities: extended CONNECT, sessions, cookies, forms, and
  per-request body-size limiting. These belong in their own packages that reference only core.

The repo already encodes this taxonomy: `IHttpExtendedConnectFeature` lives in
`Assimalign.Cohesion.Http.ExtendedConnect`, not in core, and the transport stays decoupled from
it. This package restores that discipline for the body-size feature. The enforcement itself —
the wire-level cap with 413 semantics — is *not* a feature and stays transport-owned in
`Http.Connections` (`HttpConnectionListenerLimits.MaxRequestBodySize`): the security guarantee must hold
with zero optional packages installed.

## How it works

`HttpRequestLimits.CreateMaxRequestBodySizeInterceptor()` returns a stateless
`IHttpExchangeInterceptor` the composition root registers on the server's listener options. Per
request, its `AfterRequestHead` hook attaches an internal `HttpMaxRequestBodySizeFeature` to the
exchange's feature collection.

The feature is a **write-through view over the parse context**, not a copy:

- `MaxRequestBodySize` reads and writes `HttpExchangeInterceptorRequestContext.MaxRequestBodySize` —
  the very value the transport enforces. There is no second source of truth.
- `IsReadOnly` delegates to the context's transport-owned freeze flag
  (`IsMaxRequestBodySizeReadOnly`). The transport freezes the knob after the last
  `AfterRequestHead` hook runs, as it starts consuming the body — `BeforeRequestBody` hooks
  already observe the frozen value. Because the feature holds no frozen copy of its own, any
  shift in when the transport begins that read (buffered today, first-byte once the
  streaming-body rework lands) is invisible to this package and to feature consumers: the
  contract ("adjust before the body is read; observe any time") is stable while the transport's
  definition of "read" evolves.

The transport keeps the context alive until the request body is consumed (documented on
`HttpExchangeInterceptorRequestContext`), so the view never dangles.

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

Those three are exactly the `IHttpExchangeInterceptor` surface, which is why the escalation to a
compile-time shared seam (in core, shared by all future parse-time features) is justified. New
capabilities that only need one-way post-parse publication should still prefer the Items-key
bridge.

## Ordering and defaults

Register this package's interceptor **first**. Its `AfterRequestHead` hook attaches the feature;
later interceptors' `AfterRequestHead` hooks may then look it up (or simply write the context
knob — same store).
The web host (`Assimalign.Cohesion.Web.Hosting`) installs it by default so every request carries
the typed feature — the seam is now invoked on all three parse paths (h1, h2, h3; #819), so the
feature is attached uniformly regardless of protocol (enforcement of the cap remains h1-only —
see "Protocol coverage" below). The raw transport remains lean (zero interceptors ⇒ no
per-request context or feature allocation).

## Feature identity

`Name` is `"Assimalign.Cohesion.Http.MaxRequestBodySize"` — kept byte-identical to the value the
transport historically used, because the string is the feature collection's dictionary key and
any name-keyed consumer would otherwise silently miss.

## Transport integration (as implemented)

The migration from the original in-core placement is complete; the pieces sit as follows:

1. **Contract here.** `IHttpMaxRequestBodySizeFeature` lives in this package's `src/`. Both
   assemblies use the `Assimalign.Cohesion.Http` namespace (recorded csproj deviation), so the
   move was source-compatible for consumers; only project references changed.
2. **Transport enforces, never seeds.** `Http.Connections` carries no body-size feature of its
   own: `HttpConnectionListenerOptions.Interceptors` is snapshotted to an array when the
   listener is constructed (post-construction registrations are inert — no racing the accept
   loops); each transport builds one `HttpExchangeInterceptorRequestContext` per request (read-only
   `Headers` view via `AsReadOnly()`), runs `AfterRequestHead` hooks after the head is assembled,
   freezes the knob, runs `BeforeRequestBody` hooks (skipped for CONNECT; on h1 they precede the
   automatic `Expect: 100-continue` solicitation), chains `AfterRequestBody` hooks over the
   materialized stream, and flows the hook-populated feature collection into the exchange through
   the context constructors' `features` parameters. On h1 the
   parser does this inline and enforces whatever cap remains (413); on h2/h3 the shared
   `HttpRequestInterceptorPipeline` does it at the context-construction site (no cap enforcement
   yet — see "Protocol coverage"). `HttpRequestRejectedException` is caught ahead of the
   wire-failure classifier and answered with the protocol-appropriate wire behavior (h1 minimal
   status response + close; h2 `RST_STREAM(CANCEL)`; h3 stream abort). Zero registered interceptors
   keeps the exact pre-seam fast path (no context, no feature, no hook dispatch).
3. **Stale artifacts cleaned.** The never-shipped `CreateFeatures` factory references in
   `TransportHttpContext` and the Connections `DESIGN.md` were replaced by the interceptor
   documentation.
4. **Web.Hosting installs by default.** `WebApplicationServerBuilder` registers
   `HttpRequestLimits.CreateMaxRequestBodySizeInterceptor()` ahead of all user configuration,
   so it holds interceptor slot 0.
5. **Tests.** The transport suite exercises the seam with local doubles on all three protocols
   (h1: attach / cap-raise / cap-lower / wrap / reject / freeze / read-only headers / CONNECT skip
   / snapshot inertness; h2 + h3: attach / wrap / reject → RST_STREAM/stream-abort / freeze /
   read-only headers / CONNECT skip / empty-body / lowered-cap no-reject / fast path); this
   package's suite covers the feature contract; the h1 limit-rejection suite is unchanged because
   enforcement never moved.

## Protocol coverage (honest gaps)

- The interceptor **seam** is now wired into **all three** request paths — HTTP/1.1, HTTP/2, and
  HTTP/3 (#819) — so this package's `AfterRequestHead` hook attaches the typed
  `IHttpMaxRequestBodySizeFeature` on every request regardless of protocol, and the feature is
  visible from the first middleware onward on h2/h3 exactly as on h1.
- **Cap *enforcement* remains HTTP/1.1-only.** HTTP/2 dispatches a request at header completion
  and streams the body under flow-control backpressure (h2: `Http2Stream.CreateContextAsync` runs
  at the frame pump's END_HEADERS dispatch, so `AfterRequestHead` hooks run before the application
  observes any body octet); HTTP/3 drains the request stream before header decode, so its
  `AfterRequestHead` hook runs before the body is *exposed*, not before it is *received*. On both,
  no hard body-size cap is
  enforced yet — h2 bounds buffering via flow-control backpressure and h3 via QUIC flow control
  (see `HttpConnectionListenerLimits.MaxRequestBodySize`; the hard cap is tracked follow-up
  work). A hook that lowers the cap on h2/h3 therefore changes the value the feature reports
  without rejecting the body. Nothing in this package should be read as implying h2/h3 body
  *protection* exists yet — only the typed feature and the hook plumbing do.
- Data-rate limits and the middleware-visible pre-read override window depend on the
  streaming-body rework (#810).

## AOT posture

No reflection, no codegen. Feature lookup is the ordinal-string dictionary read of
`HttpFeatureCollection`; the interceptor is a plain interface dispatch; the feature is one small
allocation per request, only when the interceptor is registered.

## Non-goals

- **Enforcement.** The wire-level cap and its 413 semantics are transport-owned
  (the per-registration `Http1Limits`); this package only observes and adjusts the per-request value.
- **Other limit knobs (request line, header count/size, timeouts).** Those are connection-wide
  policy on the per-version listener limits with no per-request story; they gain typed features here only if
  a real per-request consumer appears.
- **Client-side limits.** This is a server-side surface.
