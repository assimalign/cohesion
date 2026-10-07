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

The repo encodes this taxonomy elsewhere too: feature packages such as
`Assimalign.Cohesion.Http.ProtocolUpgrade` and `Assimalign.Cohesion.Http.InterimResponses` own their
contracts, and the transport never references them. (A contract only moves into core when the
transport itself must implement it — `IHttpTlsConnectionFeature`, and `IHttpExtendedConnectFeature`
once extended CONNECT gained its tunnel; see core Http DESIGN.) This package restores that
discipline for the body-size feature. The enforcement itself —
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
  (`IsMaxRequestBodySizeReadOnly`). The transport freezes the knob when it starts consuming the
  body. On HTTP/1.1 (#810) and HTTP/3 (#1066) that is the **first read of the streamed body**:
  the request is dispatched at head and the body read lazily, so the writable window spans the
  head hooks, the `BeforeRequestBody` hooks, *and* every middleware / endpoint that runs before the
  body is read — which is what makes the middleware-visible pre-read override real. (On HTTP/2 the
  pipeline still freezes before the body is exposed, so its `BeforeRequestBody` hooks observe the
  frozen value.) Because the feature holds no frozen copy of its own, that per-version timing
  is invisible to this package and to feature consumers: the contract ("adjust before the body is
  read; observe any time") is stable while the transport's definition of "read" evolves.

The transport keeps the context alive until the request body is consumed (documented on
`HttpExchangeInterceptorRequestContext`), so the view never dangles.

### Why a typed seam here, rather than an Items-key bridge

An `Items`-key bridge — the transport publishes a convention-named `IHttpContext.Items` value with
**no shared symbol**, and a package interprets it (what extended CONNECT used before its tunnel) — is
the right shape for one-way publication of an immutable value after parse. This package needs three
things such a bridge cannot express:

1. **Mutation with enforcement coupling** — the feature must write a value the transport then
   enforces mid-parse, not merely read one it published.
2. **Pre-dispatch attachment** — the feature must exist on `Features` before the first
   middleware runs, without the transport knowing the feature type.
3. **Stream wrapping** — returning a replacement body stream has no Items-key analogue.

Those three are exactly the `IHttpExchangeInterceptor` surface, which is why the escalation to a
compile-time shared seam (in core, shared by all future parse-time features) is justified. A
capability that only needs one-way post-parse publication can still use an `Items` key.

## Ordering and defaults

Register this package's interceptor **first**. Its `AfterRequestHead` hook attaches the feature;
later interceptors' `AfterRequestHead` hooks may then look it up (or simply write the context
knob — same store).
The web host (`Assimalign.Cohesion.Web.Hosting`) installs it by default so every request carries
the typed feature — the seam is now invoked on all three parse paths (h1, h2, h3; #819), so the
feature is attached uniformly regardless of protocol (the cap is enforced on all three — see
"Protocol coverage" below). The raw transport remains lean (zero interceptors ⇒ no
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
   freezes the knob (h2 right after those hooks; h1 and h3 at the first body read), runs
   `BeforeRequestBody` hooks (skipped for CONNECT; on h1 they precede the
   automatic `Expect: 100-continue` solicitation), chains `AfterRequestBody` hooks over the
   materialized stream, and flows the hook-populated feature collection into the exchange through
   the context constructors' `features` parameters. On h1 the
   parser does this inline and enforces whatever cap remains (413); on h2/h3 the shared
   `HttpRequestInterceptorPipeline` does it at the context-construction site: h2 enforces the
   frozen value the pipeline returns (413), and h3's lazy body enforces the cap the way h1's does
   (413 — see "Protocol coverage"). `HttpRequestRejectedException` is caught ahead of the
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
   read-only headers / CONNECT skip / empty-body / fast path; h2: cap-lower → 413 and cap-raise;
   h3: the h1-style freeze-at-first-read, cap-raise, and cap-lower → 413); this package's suite
   covers the feature contract; the h1 limit-rejection suite is unchanged because h1 enforcement
   never moved.

## Protocol coverage (honest gaps)

- The interceptor **seam** is now wired into **all three** request paths — HTTP/1.1, HTTP/2, and
  HTTP/3 (#819) — so this package's `AfterRequestHead` hook attaches the typed
  `IHttpMaxRequestBodySizeFeature` on every request regardless of protocol, and the feature is
  visible from the first middleware onward on h2/h3 exactly as on h1.
- **Cap *enforcement* covers all three protocols.** HTTP/1.1 and HTTP/3 (#1066) dispatch a
  request at its head and read the body lazily, and their body streams enforce whatever cap the
  feature holds at the first read (413). HTTP/2 dispatches a request at header completion
  (`Http2Stream.CreateContextAsync` runs at the frame pump's END_HEADERS dispatch, so
  `AfterRequestHead` hooks run before the application observes any body octet) and enforces the
  cap as frozen after those hooks (#1048): a declared `content-length` over it is answered `413`
  before the request is dispatched, and a body that grows past it is answered on receipt — `413`
  when the response has not started, a stream reset when it has. A hook that lowers the cap
  therefore rejects an oversized body on every protocol; one that raises it admits the larger
  body.
- **The middleware-visible pre-read override window is live on HTTP/1.1 (#810) and HTTP/3
  (#1066).** Both transports stream the request body and freeze the cap at the first body read, so
  middleware and endpoints — not just head-hook interceptors — can adjust `MaxRequestBodySize`
  through this feature before the body is read, and the transport enforces whatever value remains
  (413). The h2 pipeline freezes the knob at dispatch, so on HTTP/2 the feature is read-only from
  the first middleware onward and only head hooks can adjust the cap; opening the same window
  there is later work. Minimum-data-rate limits (`MinRequestBodyDataRate` /
  `MinResponseDataRate`) also landed with #810, but as transport-owned limits
  (`HttpConnectionListenerLimits`) enforced on HTTP/1.1 only, not features surfaced by this
  package.

## AOT posture

No reflection, no codegen. Feature lookup is the ordinal-string dictionary read of
`HttpFeatureCollection`; the interceptor is a plain interface dispatch; the feature is one small
allocation per request, only when the interceptor is registered.

## Non-goals

- **Enforcement.** The wire-level cap and its 413 semantics are transport-owned
  (the per-registration `Http1Limits` / `Http2Limits` / `Http3Limits`); this package only observes
  and adjusts the per-request value.
- **Other limit knobs (request line, header count/size, timeouts).** Those are connection-wide
  policy on the per-version listener limits with no per-request story; they gain typed features here only if
  a real per-request consumer appears.
- **Client-side limits.** This is a server-side surface.
