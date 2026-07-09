# Assimalign.Cohesion.Http.Connections — Design

This document captures the design intent behind the shipped HTTP transport
surface. It is intentionally focused on the design decisions a future
reader (or future Claude session) would otherwise have to re-derive from
diffs. `DESIGN_SUGGESTION.md` in this same folder is a separate,
forward-looking proposal for a multiplex-aware refactor; this file
describes the surface as it ships today.

## Transport seam: consuming `Assimalign.Cohesion.Connections`

### What it is

This package no longer carries (or inherits) any transport machinery of
its own. The deleted `Assimalign.Cohesion.Transports` stack
(`ITransport`, `ITransportConnection`, `ITransportConnectionContext`,
`TransportConnectionPipe`, `ServerTransport<T>`, the `Items`/`IsSecure`
extensions) has been replaced by consumption of the
`Assimalign.Cohesion.Connections` contracts:

- `IConnectionListener` produces live `IConnection`s — the connection
  **is** the duplex pipe (`Input`/`Output` directly on it; there is no
  separate "context" and no `OpenAsync` step on the transport).
- `IMultiplexedConnectionListener` produces `IMultiplexedConnection`s
  whose accepted/opened streams are themselves `IConnection`s with a
  per-stream `Direction`.

HTTP **consumes** these contracts; it never extends them. The HTTP-side
contracts (`IHttpConnectionListener`, `IHttpConnection`,
`IHttpConnectionContext`) are standalone interfaces that wrap a
connection and project HTTP semantics over it.

### Structural listener registration

`HttpConnectionListenerOptions` binds concrete listeners to protocols
with shape safety enforced at the seam:

```csharp
HttpConnectionListener listener = HttpConnectionListener.Create(options =>
{
    options.UseHttp1(tcpListener);                 // IConnectionListener
    options.UseHttp2(tlsTcpListener, http2 =>      // IConnectionListener (TLS pre-composed)
    {
        http2.Limits.MaxStreamsPerConnection = 256;
    });
    options.UseHttp3(quicListener, http3 =>        // IMultiplexedConnectionListener
    {
        http3.QPack.MaxTableCapacity = 4096;
    });
});
```

- `UseHttp1` / `UseHttp2` accept an `IConnectionListener` (or a
  `Func<IConnectionListener>` materialized when the
  `HttpConnectionListener` is constructed) and **gate on capabilities,
  never on protocol identity**: the listener's `ConnectionCapabilities`
  must report `Delivery == Stream`, `IsReliable`, and `IsOrdered`, else
  an `ArgumentException` describes the capability mismatch.
  `ConnectionProtocol` is diagnostics-only and is never branched on.
- `UseHttp3` accepts an `IMultiplexedConnectionListener` — the parameter
  type itself is the shape gate, so no runtime capability check is
  needed for stream multiplexing.

### Per-version options, captured per registration

Every `Use*` method has an overload taking a configure callback for that
version's options type — `Http1ConnectionListenerOptions`,
`Http2ConnectionListenerOptions`, `Http3ConnectionListenerOptions` (all under
`Options/`) — so each protocol version owns its own configuration surface
instead of sharing one listener-wide bag. The options are captured **per
registration** at `Use*` time and closed over by that registration's
connection-factory builder; two registrations of the same version can carry
different limits. Cross-version concerns stay listener-wide on
`HttpConnectionListenerOptions`: the request/response interceptors (snapshotted
when the `HttpConnectionListener` is constructed) and `BacklogCapacity`.

Limits follow the same split. `HttpConnectionListenerLimits` is the abstract
base holding only the limits meaningful to all three versions
(`MaxRequestBodySize`, `KeepAliveTimeout`, `RequestHeadersTimeout` — each
documents where it is enforced today); the version-specific types nest inside
their options class (`Http1ConnectionListenerOptions.Http1Limits` adds the
HTTP/1.1 wire-format bounds, `Http2ConnectionListenerOptions.Http2Limits` adds
the HTTP/2 abuse caps). HTTP/3 deliberately exposes no limits surface — its
stream/flow-control limits live in the QUIC transport (see the HTTP/3
non-goals).

`BacklogCapacity` retains its bounded-channel semantics: it caps how
many accepted HTTP connections may buffer before the per-listener accept
loops wait for `AcceptOrListenAsync` to drain them.

### Accept loops and the live-connection model

`HttpConnectionListener` runs one accept loop per registered listener.
The HTTP/1.1 and HTTP/2 loops do `IConnection connection = await
listener.AcceptAsync(token)` and wrap the result in
`Http1Connection`/`Http2Connection`; the HTTP/3 loop accepts an
`IMultiplexedConnection` (with the QUIC platform guard) and wraps it in
`Http3Connection`. Because connections are already live when produced,
`IHttpConnection.Open()`/`OpenAsync` is a synchronous projection — it
constructs the protocol's connection context over the wrapped
connection; there is no transport open step to await.

The connection-is-the-pipe model shows up at three points:

- Stream parsing (HTTP/1.1, HTTP/2) adapts the duplex pipe once via
  `connection.AsStream()`.
- Graceful teardown completes `connection.Output` directly (HTTP/2
  GOAWAY + bounded stream drain, HTTP/1.1 response drain) before disposal
  (see "HTTP/2 graceful close").
- HTTP/3 reads each accepted stream's `Input` (`PipeReader`) for
  unidirectional streams and `AsStream()` for request streams.

### Audited context surface

`IHttpConnectionContext` declares only what the HTTP internals actually
consume: the endpoints plus `ReceiveAsync`/`SendAsync`. The inherited
members of the old transport context (`Pipe`, `Items`,
`ConnectionClosed`, `Close…`) were plumbing and are gone. Contexts that
have no connection-level byte stream — the HTTP/3 connection context
(whose bytes live on per-request streams) and
`NotSupportedHttpConnectionContext` — no longer fabricate a fake pipe
over `Stream.Null`; the member simply does not exist.

### TLS is a pre-composed layer, not an HTTP concern

TLS never happens inside this package. The composition root layers it
onto the listener before registration (`listener.UseTls(options)` /
`listener.Use(layer)` from the security/connections libraries), and the
layered listener's `Capabilities.Security` reports `ConnectionSecurity.Tls`.
HTTP derives its per-connection `isSecure` flag from exactly that:
`listener.Capabilities.Security == ConnectionSecurity.Tls`, captured once
per accept loop. There is no registration-time `isSecure` parameter, no
`Items`-backed handshake probe, and no OR-promotion rule — the
capability is the single source of truth, and the scheme
(`http`/`https`) flows from it.

## Per-request feature injection — request-parse interceptors

### What it is

`HttpConnectionListenerOptions.Interceptors` is the **single** injection seam the
transport exposes for code outside this package to participate in an exchange's
lifecycle — one list, one registration order, spanning the request-parse hooks
and the response hooks. The contract — `IHttpExchangeInterceptor` (implemented by
deriving from the guided `HttpExchangeInterceptor` base), the phase contexts, and
the typed rejection `HttpRequestRejectedException` — lives in core
`Assimalign.Cohesion.Http` (a generic seam, like `IHttpFeature`), so feature
packages implement hooks without referencing this transport package and this
transport never references them. The `HttpConnectionListener` snapshots the list
once at construction and partitions it by each interceptor's declared
`HttpInterceptorScopes` — request-scoped hooks and response-scoped machinery are
invoked only for interceptors that declared that phase, which keeps the zero-cost
fast paths scope-exact (a request-only default like `Http.RequestLimits` never
causes a response sink or exchange control to be constructed):

```csharp
HttpConnectionListenerOptions options = new();
options.Interceptors.Add(HttpRequestLimits.CreateMaxRequestBodySizeInterceptor());
options.Interceptors.Add(new RequestDigestInterceptor(/* parse-time hashing */));
```

Per request each transport (using the HTTP/1.1 parser as the reference; HTTP/2
and HTTP/3 run the same steps 2–5 through the shared
`HttpRequestInterceptorPipeline` — see "Protocol coverage" below) walks the
request's lifecycle hooks in order:

1. Parses the head (request line + headers) under the configured limits, then
   derives host/scheme.
2. Builds one `HttpExchangeInterceptorRequestContext` — head data, a **read-only**
   header view (`HttpHeaderCollection.AsReadOnly()`), a fresh feature
   collection, and the body-size knob seeded from the registration's
   `Http1Limits.MaxRequestBodySize` — and runs every `AfterRequestHead` hook in
   registration order.
3. Runs every `BeforeRequestBody` hook in registration order — the body is about
   to be surfaced (HTTP/1.1: as the lazy streamed body, no octet consumed yet) or
   exposed (HTTP/2 / HTTP/3). On HTTP/1.1 this precedes any `Expect: 100-continue`
   solicitation (itself lazy, at the first body read), so a hook that rejects here
   does so before the body is solicited; the knob is **not yet frozen** there — it
   freezes at the first body read, which is what opens the pre-read override
   window to middleware (see "Per-request body-size override"). On HTTP/2 /
   HTTP/3 the pipeline freezes the knob first, so their hooks observe the frozen
   value. CONNECT tunnels skip it.
4. Materializes the body stream — on HTTP/1.1 the lazy `Http1RequestBodyStream`
   (cap enforced at read time, 413 on violation); on HTTP/2 / HTTP/3 the
   already-buffered stream — and runs every `AfterRequestBody` hook in
   registration order, each receiving the previous result — the last registered
   interceptor produces the outermost wrapper. CONNECT tunnels skip body hooks;
   empty bodies still run them.
5. Constructs the exchange, flowing the hook-populated feature collection in
   through the context constructors (the previously-dormant `features`
   parameters on `Http1Context`/`Http2Context`/`Http3Context` and
   `TransportHttpContext` now forward it).

**Zero registered interceptors is a true fast path**: no context, no feature
collection, no read-only header view, no hook dispatch — the parser enforces
the listener-wide limits exactly as it did before the seam existed.

> Historical note: an earlier revision of this document described a
> `HttpConnectionListenerOptions.CreateFeatures` factory. That factory was
> never implemented — the doc ran ahead of the code — and the interceptor seam
> supersedes it: `AfterRequestHead` + `Features.Set` covers feature seeding and
> adds cap adjustment, stream wrapping, and typed rejection that a
> feature-collection factory could never express.

### Why per-request, not per-connection

Unchanged from the original design reasoning: `IHttpContext` is
`IAsyncDisposable` and tears down at the end of every request, so per-request
scoping gives features deterministic cleanup; HTTP/2 and HTTP/3 multiplex many
requests over one connection, so connection-scoped mutable state is a data race
by construction; and application code reasons in requests, not connections.
Interceptor *instances* are the inverse: registered once on the options,
snapshotted into an array when the `HttpConnectionListener` is constructed
(later registrations are inert — no racing the accept loops), and shared across
every connection and request. Implementations must therefore be stateless and
thread-safe; all per-request state belongs in the context's feature collection.

### Exception classification on the parse path

- **`HttpRequestRejectedException`** (4xx/5xx-constrained) is the sanctioned way
  for a hook to refuse a request; each transport answers it with the wire
  behavior appropriate to its framing, ahead of the wire-level classifier:
  - **HTTP/1.1** — caught in `Http1ConnectionContext.TryReadRequestAsync`,
    answered with a minimal bodyless status response, and the connection is
    closed (never reused: remaining wire state is indeterminate).
  - **HTTP/2** — `Http2ConnectionContext.TryDispatchStreamAsync` translates it
    into an `Http2StreamException` carrying `CANCEL`, so the frame pump emits
    `RST_STREAM(CANCEL)` (RFC 9113 §5.4.2), removes the stream (reclaiming its
    receive-window debt), and keeps serving the connection's other streams.
    HTTP/2 has no `REQUEST_REJECTED` code; `CANCEL` is the neutral per-stream
    termination already used by the transport's application-cancel path
    (`IHttpContext.Cancel`), and deliberately avoids `REFUSED_STREAM`'s "safe to
    retry" promise, which could amplify load against the very DoS-mitigation
    interceptors this seam hosts.
  - **HTTP/3** — `Http3ConnectionContext.ReadRequestAsync` aborts the request
    stream and drops it (RFC 9114 §4.1), leaving the QUIC connection and its
    other streams intact. The semantically exact code is `H3_REQUEST_REJECTED`,
    but the `IConnection` abort contract resets with the transport's configured
    default stream error code rather than a per-call one — a limitation of the
    connection abstraction, not the seam.

  In every case the request-parse interceptor pipeline has already torn down the
  partially-built body-wrapper chain and every hook-attached feature before the
  rejection surfaces, so the transport's rejection handler only performs the wire
  action, never the cleanup.
- **`IOException`-family exceptions** thrown by a hook are indistinguishable
  from wire failures and get silently classified as such (connection dropped,
  no response). This is a documented hazard, not a feature: hooks must use the
  typed rejection for control flow.
- **Anything else** propagates — programmer errors are not masked, matching the
  receive-loop failure-isolation philosophy.

Hooks run inline on the parse path at a point where the request-headers
deadline has been disarmed (on HTTP/2 that path is the connection's single
frame pump), so they must be CPU-only; a blocking hook stalls the whole
connection — every multiplexed stream on it — and pins a thread-pool thread.

### Disposal contract

When `IHttpContext.DisposeAsync` runs, the transport walks the effective
feature collection and disposes every feature implementing `IAsyncDisposable`
or `IDisposable` (async preferred; one throwing feature does not abort the
walk; the list is snapshotted before disposal so a mutating `DisposeAsync`
cannot break iteration). Features attached by request-parse hooks and by middleware are
treated identically. A body-stream wrapper owns the stream it wraps: disposing
the outermost stream (which the exchange's disposal triggers via
`Request.Body.Dispose()`) must dispose the whole chain.

The contract also covers requests that never become an exchange. If the parse
fails **after** the head hooks (`AfterRequestHead`) ran — a limit rejection (413/431), a hook rejection,
a malformed body, a wire failure, or a timeout — no `IHttpContext` exists to
own the disposal walk, so the invocation site itself tears down the
partially-built wrapper chain and disposes every hook-attached feature (same
walk semantics) before the failure surfaces. On HTTP/1.1 that is the parser; on
HTTP/2 and HTTP/3 it is the shared `HttpRequestInterceptorPipeline`, which
disposes the chain and features in its own `catch` before rethrowing to the
transport's rejection handler. Hook-attached disposables therefore never leak on
the rejection paths an attacker can drive for free (e.g. an oversized
`Content-Length` declaration, rejected before any body byte is read).

### Feature-collection plumbing

The parser hands its `HttpFeatureCollection` to the context constructor, which
uses it **directly** — no defaults-wrapper layer, which would add a second
dictionary probe to every `Get` on the hot path. A `null` collection (the
fast path) gets a fresh empty one; a foreign `IHttpFeatureCollection`
implementation is wrapped as a read-through defaults source for safety.

### Protocol coverage

The hooks are wired into **all three** request paths — HTTP/1.1, HTTP/2, and
HTTP/3 — so a registered interceptor (the default `Http.RequestLimits` feature,
a parse-time digest, request decompression, …) participates uniformly no matter
which protocol served the request. A single shared helper,
`HttpRequestInterceptorPipeline`, drives the h2/h3 invocation with the same
ordering, CONNECT-skip, empty-body, freeze, rejection, and failure-path
disposal semantics as the h1 parser; each transport calls it at the point its
request head is assembled into a context (`Http2Stream.CreateContextAsync` from
the frame pump's END_HEADERS dispatch, `Http3ConnectionContext.ReadRequestAsync`)
and flows the hook-populated feature collection into the exchange through the
(previously dormant) `features` parameter on `Http2Context` / `Http3Context`.
The knob each context is seeded from is the registration's shared
`HttpConnectionListenerLimits.MaxRequestBodySize` (`Http1Limits` / `Http2Limits`
/ `Http3Limits`), so the same interceptor observes the same knob everywhere.

The **per-protocol timing is documented on the seam contract**: h1 runs the head
hook before any body octet is consumed from the wire, so a lowered cap precedes
enforcement. HTTP/2 dispatches a request as soon as its header block completes
and streams the body incrementally under flow-control backpressure (see "HTTP/2
request-body flow control and backpressure"), so head hooks run before the
application observes any body octet — DATA already in flight sits buffered in
the stream's flow-control-bounded pipe — and a body hook wraps the live
streaming body stream (forward-only, exactly what the hook contract requires
wrappers to tolerate). HTTP/3 still drains a request stream before header
decode, so its hooks run before the body is *exposed*, not before it was
*received*.

The **cap-enforcement posture is unchanged by the wiring**: no hard body-size
cap is enforced on h2 or h3 yet (h2 bounds body buffering via flow-control
backpressure, h3 via QUIC flow control — the hard cap and connection timeouts
remain tracked follow-up work, per `HttpConnectionListenerLimits`). A hook that
lowers the cap on those transports today adjusts only the value hook-attached
features expose; the seam wires the hook *invocation*, and each request's parse
context already carries the frozen post-hook value for those paths to consume
when they gain enforcement.

### AOT posture

No reflection, no runtime code generation. Hook dispatch is interface calls
over a snapshotted array; the context is one small allocation per request,
only when at least one interceptor is registered.

## IsSecure: capability-derived, single-source

### What it is

`HttpContext.ConnectionInfo` reports the request scheme
(`http`/`https`) from a single per-connection `isSecure` flag derived
at the transport seam:

```csharp
bool isSecure = listener.Capabilities.Security == ConnectionSecurity.Tls;
```

captured once per accept loop in `HttpConnectionListener` and passed
down to the protocol connection (`Http1Connection`, `Http2Connection`,
`Http3Connection`) as a constructor argument.

### Why a capability, not a hint + probe

A previous iteration combined a registration-time `isSecure` boolean
with a runtime probe of an `Items`-backed
`ITransportConnectionContext.IsSecure` extension
(`effective = registrationHint || transportReports`). Both signals are
gone, replaced by the listener's declared `ConnectionCapabilities`:

- TLS is composed onto the listener **before** it is handed to HTTP
  (`listener.UseTls(...)` / `listener.Use(layer)`), and the layering
  machinery rewrites `Capabilities.Security` to `ConnectionSecurity.Tls`
  for both the listener and the connections it produces. The capability
  *is* the handshake's outcome at the only point HTTP can observe it.
- An operator hint can contradict reality (declared secure, plaintext
  transport); a capability cannot — it is asserted by the layer that
  actually performs the handshake. Removing the OR rule removes the
  possibility of the two signals disagreeing.
- QUIC's always-on TLS needs no special case: a QUIC listener simply
  reports `Security = Tls` like any other secured listener, and HTTP/3
  derives the same way HTTP/1.1 and HTTP/2 do.

### Non-goals

- **Mid-connection upgrade (STARTTLS / `Upgrade: TLS/1.0`).** The flag
  is captured per accept loop and fixed for the connection's lifetime.
  RFC 2817 in-band TLS upgrade over HTTP/1.1 would require explicit
  re-construction of the connection and is intentionally out of scope.
- **Rich TLS metadata** (client certificate, ALPN, cipher suite).
  Future work; it belongs on the connections/security layer (where the
  handshake runs), surfaced through a typed seam rather than through
  HTTP-transport plumbing.

## Response streaming: raw body sink behind the response-interceptor seam

### What it is

The baseline response path buffers a whole response and serializes it once
(`SendAsync` reads `Response.Body` and writes it in a single HEADERS+DATA
sequence). That cannot express Server-Sent Events, long-lived progress feeds, or
memory-efficient large responses, because the peer sees nothing until the handler
returns. The streaming write path adds the ability to start a response and write it
incrementally — but **it is deliberately not wired into the transport as a
streaming feature.** Instead the transport exposes a generic seam and a raw body
sink; the streaming/SSE capability is a feature package that plugs in.

This keeps the transport free of any streaming or SSE dependency: the streaming
API, its state machine, and the SSE wire format all live in feature packages
(`Assimalign.Cohesion.Http.Streaming`, `Assimalign.Cohesion.Http.ServerSentEvents`)
that this library never references.

### The two moving parts

- **`HttpResponseBodyStream`** — the transport's raw response body sink, a write-only
  `System.IO.Stream`. Its abstract base owns the response-lifecycle state machine
  (commit-the-head-once on the first write/flush, idempotent completion) and forwards
  the framing to per-protocol subclasses: `Http1ResponseBodyStream` (chunked transfer
  coding), `Http2ResponseBodyStream` / `Http3ResponseBodyStream` (`DATA` frames). This
  is where the header-commit timing and the wire framing live.
- **`IHttpExchangeInterceptor`** (core Http) + `HttpConnectionListenerOptions.Interceptors`
  — the symmetric counterpart to the request-interceptor seam, now a **lifecycle-hook
  set** rather than a single invocation point. At context setup, when any response
  interceptor is registered, the transport creates the per-protocol sink and exchange
  control, builds a `HttpExchangeInterceptorResponseContext` exposing them as `ResponseBody` /
  `Control`, retains that context for the exchange's lifetime, and runs the
  `BeforeResponse` hooks. The same context is re-presented to the later hooks:
  `BeforeResponseHeadAsync` fires exactly once immediately before the final head is
  committed (buffered send or streaming first-commit, whichever happens first —
  the last point at which status/headers can be mutated or an interim response
  emitted; the transport re-reads the exchange's state after the hooks run), and
  `AfterResponseAsync` fires exactly
  once after the final response is fully written (never for an aborted or taken-over
  exchange). A feature package's interceptor wraps the sink or the control in a typed
  feature and installs it on `context.Features`; the transport neither knows nor cares
  what feature. `BeforeResponse` runs inline on the parse/dispatch path (on HTTP/2 the
  frame pump) and must be CPU-only; the two async hooks run on the exchange's send
  path where awaiting is safe.

The response-scoped partition of the single interceptor list is threaded to all
three protocol connections. **Zero response-scoped interceptors is a true fast
path**: no sink or control is created, no interception context is retained, the
later hook invokers are no-ops, and the buffered response path runs exactly as
before — even when request-scoped interceptors are registered.

### The `SendAsync` inversion

The connection loop still calls `connectionContext.SendAsync(context)` after the
handler returns. Each transport's `SendAsync` branches at the top on the exchange's
internal directive: a taken-over exchange (HTTP/1.1 only) or an
application-cancelled one (`IHttpContext.Cancel` — abort is authored on the
application surface, never the seam) never writes a response — takeover
suppresses the send entirely, a cancel maps to the version's wire rejection (h1
ends the connection after the exchange, h2 `RST_STREAM(CANCEL)`, h3 stream
abort). Otherwise,
if a response feature wrote to the raw sink (`ResponseBodySink is { HasStarted: true }`),
`SendAsync` **finalizes** the sink — emitting the terminating zero-length chunk
(HTTP/1.1) or the empty `END_STREAM` DATA frame (HTTP/2) — instead of writing a
second buffered response. If the sink was never written (or none exists), the
buffered path fires the `BeforeResponseHead` hooks, re-reads the directive (a hook
may have aborted or taken over), and only then writes. The wire terminator is thus
emitted by the transport when it finalizes the exchange, not by the feature, and the
`AfterResponse` hooks close out every successfully-written exchange.

### Per-protocol framing

- **HTTP/1.1 — chunked transfer coding (RFC 9112 §7.1).** When the handler left
  `Content-Length` unset (the streaming case), `Http1ResponseBodyStream` adds
  `Transfer-Encoding: chunked` and wraps every write in a chunk; the finalize emits
  the terminating zero-length chunk. Chunked framing is self-delimiting, so the
  connection stays keep-alive. A HEAD response commits the head but writes no body.
  `Http1MessageWriter.WriteHeadAsync` is shared by the buffered and streaming paths.
- **HTTP/2 — incremental DATA frames (RFC 9113).** The HEADERS block is written
  **without** a synthesized `Content-Length` (the body is delimited by
  `END_STREAM`); each write emits one or more DATA frames split on the peer's
  `MAX_FRAME_SIZE`, each flushed through the transport; finalize emits an empty DATA
  frame carrying `END_STREAM`.
- **HTTP/3 — incremental DATA frames (RFC 9114).** Same shape over the QUIC request
  stream (a HEADERS frame with no `Content-Length`, then DATA frames). The body is
  delimited by the QUIC stream end, so finalize only flushes.

### Backpressure (flow control)

- **HTTP/2** multiplexes over one TCP stream and tracks flow-control windows in
  software, so send-side backpressure is enforced here. `WriteStreamingDataAsync`
  calls `AcquireSendWindowAsync`, which consumes credit from **both** the
  connection-level and stream-level send windows (RFC 9113 §5.2) and, when both are
  exhausted, parks on a `TaskCompletionSource` signal until credit is replenished by
  an inbound `WINDOW_UPDATE` (or a `SETTINGS_INITIAL_WINDOW_SIZE` increase). Those
  frames are processed by the **background frame pump** (see the HTTP/2 flow-control
  section below), which runs concurrently with the application handler — so a parked
  writer is always unblocked by the pump, regardless of how the host dispatches
  requests. Send-window consume/replenish shares the connection's `_syncRoot` with
  the stream table (never held across an `await`); signal completions run
  asynchronously so a parked writer never resumes inline under the lock. If the pump
  exits (wire failure, connection error, teardown) send credit is marked permanently
  closed and a parked writer fails with a wire-level `IOException` instead of
  hanging on a signal nothing will ever complete.
- **HTTP/3** rides QUIC, whose per-stream flow control is applied by the transport on
  the underlying `Stream.WriteAsync`, so no software window accounting is needed here.

A completed streamed response performs the same cleanup as the buffered path: the
fully-closed stream is removed from the stream table (reclaiming any undrained
receive-window debt via `RemoveStreamAsync`), and a stream whose peer half is still
open is reset with `NO_ERROR` to stop the remaining request body and reclaim the
concurrency slot.

### AOT posture

No reflection, no runtime code generation. Chunk framing is byte arithmetic; the
HTTP/2 flow controller is lock + `TaskCompletionSource` signaling.

### Non-goals

- **Data-rate (minimum-throughput) limits** on the *HTTP/2 / HTTP/3* streamed body — still
  deferred (h2 paces via flow control, h3 via QUIC). The **HTTP/1.1** streaming write path *does*
  now enforce `MinResponseDataRate` (a slow reader that fails to drain the response is abandoned) —
  see "HTTP/1.1 request-body streaming and data rates" below.
- **HEAD-body suppression on HTTP/2 / HTTP/3.** Only the HTTP/1.1 path suppresses the
  body for HEAD; the h2/h3 buffered paths never did, and the sink matches their
  existing behavior.
- **A streaming/SSE dependency in this library.** By design — the feature packages
  own it; this transport only exposes the sink and the interceptor seam.

## Interim (1xx) responses and `Expect: 100-continue`

### What it is

RFC 9110 §15.2 lets a server emit one or more **interim** (`1xx`) responses ahead
of the single final response — most usefully `100 Continue` (RFC 9110 §10.1.1,
the `Expect: 100-continue` handshake large-upload clients rely on) and
`103 Early Hints` (RFC 8297, `Link` fields that let a client start fetching
sub-resources before the final response is ready). Before this, the response
write paths on all three versions assumed exactly one response per exchange, so
neither an application nor the transport itself could put a `1xx` on the wire.

Two capabilities are added, and they are independent:

1. **Automatic `Expect: 100-continue`** on HTTP/1.1 — a transport behavior, no
   application involvement (below).
2. **Application-emitted interim responses** through the response-interceptor seam,
   on all three versions (below).

### The seam: interim writes on `IHttpExchangeControl`

Application-emitted interim responses follow the repository's feature-package
convention (the same as `Http.Streaming` and `Http.ProtocolUpgrade`): the transport
exposes only the **generic exchange control**, and the typed feature lives in a
separate package. The transport does **not** define or install an interim
*feature* — that would couple the protocol core and the transport to the
capability.

- The **core** (`Assimalign.Cohesion.Http`) defines `IHttpExchangeControl` — the
  single per-exchange control surface on
  `HttpExchangeInterceptorResponseContext.Control` — whose interim-write members
  (`CanWriteInterimResponse` / `WriteInterimResponseAsync`) carry this capability.
  Unlike the control's takeover members (HTTP/1.1-only), interim writes are
  offered on **all three** versions.
- The **transport** ships a per-protocol internal control
  (`Http1ExchangeControl` / `Http2ExchangeControl` / `Http3ExchangeControl`) and
  passes it into `RunResponseInterceptors` alongside the response-body sink —
  created only when at least one response interceptor is registered (the same gate
  as the sink), so the zero-interceptor path allocates nothing extra and
  `context.Features` stays empty on that path.
- The **feature package** (`Assimalign.Cohesion.Http.InterimResponses`) owns the
  application-facing `IHttpInterimResponseFeature`, the interceptor that wraps the
  control and installs the feature, and the `context.InterimResponse` /
  `SendEarlyHintsAsync` / `SendContinueAsync` ergonomics. The transport never
  references it.

The interim-write members of the control are deliberately small:

- `bool CanWriteInterimResponse` — `true` while an interim can still precede the
  final response. It flips to `false` once the final response head is committed (a
  streamed body started, or on HTTP/1.1 the connection was taken over by a protocol
  upgrade). The feature's `IsInterimResponseSupported` forwards to it — the
  **report-don't-throw** discoverability path.
- `ValueTask WriteInterimResponseAsync(HttpStatusCode statusCode, IHttpHeaderCollection? headers = null, …)`
  — emits one interim response. The status MUST be `1xx` and MUST NOT be `101`
  (an `ArgumentOutOfRangeException` otherwise); `101 Switching Protocols` is a
  connection transition owned by `Assimalign.Cohesion.Http.ProtocolUpgrade`, not
  an interim response. Emitting **after** the final response has started is an
  ordering error and throws `InvalidOperationException`. `headers` is `null` for a
  bodyless `100`; a `103` typically carries only `Link`.

The shared status-code rules live in `HttpInterimResponseRules`
(`ValidateInterimStatusCode` for the control impls, `EnsureFinalStatusCode` for the
final-response guard below), so all three engines classify `1xx` identically.

### Per-transport wire emission

- **HTTP/1.1** — `Http1ExchangeControl` writes the interim status line and
  fields straight onto the connection stream via
  `Http1MessageWriter.WriteInterimResponseAsync`
  (`HTTP/1.1 <code> <reason>` CRLF, one field line per value — so a multi-valued
  `Link` is expressed without comma-folding — then the blank line, then a flush).
  No `Content-Length` is written (an interim carries no body). An HTTP/1.1 exchange
  owns its whole connection and the handler is the sole writer for its duration,
  so the interim bytes simply precede the final response bytes; no interleaving
  discipline is needed.
- **HTTP/2** — `Http2ExchangeControl` delegates to
  `Http2ConnectionContext.WriteInterimResponseAsync`, which encodes the field
  section with `HPackEncoder.EncodeInterimResponseHeaders` (the `1xx` `:status`
  with **no** synthesized `Content-Length`) and writes it as an additional HEADERS
  block **without** `END_STREAM` (RFC 9113 §8.1), holding the connection write gate
  (`Http2WriteScheduler`) at the stream's effective priority for the whole
  HEADERS [+ CONTINUATION…] sequence so it never interleaves with the pump's
  control frames or another stream's response (RFC 9113 §4.1). The stream's local
  half is left open — the final HEADERS(+DATA) with `END_STREAM` follows on the
  same stream.
- **HTTP/3** — `Http3ExchangeControl` delegates to
  `Http3ConnectionContext.WriteInterimResponseAsync`, which encodes the field
  section with `Http3HeaderCodec.EncodeInterimResponseHeaders` (QPACK, `1xx`
  `:status`, no `Content-Length`) and writes an additional HEADERS frame on the
  request stream ahead of the final HEADERS frame (RFC 9114 §4.1). The request
  stream is single-writer for the response direction and QUIC applies its own
  per-stream flow control, so the interim frame simply precedes the final frames.

H2/H3 peers may receive several interim HEADERS, all before the final one — the
capability can be called repeatedly.

### The `1xx`-as-final-status guard

A `1xx` is never a valid *final* response status. Every final-response write path
funnels through `HttpInterimResponseRules.EnsureFinalStatusCode`, which throws a
descriptive `InvalidOperationException`: HTTP/1.1 in the shared
`Http1MessageWriter.WriteHeadAsync` (buffered + streaming), HTTP/2 in `SendAsync`
and `WriteStreamingHeadersAsync`, HTTP/3 in `SendAsync` and the streaming sink's
`CommitHeadersAsync`. The sole `1xx` that legitimately ends an exchange —
`101 Switching Protocols` — is finalized out-of-band by the HTTP/1.1
protocol-upgrade path (its `SendAsync` is suppressed via `ResponseFinalized`), so
it never reaches the guard; HTTP/2 and HTTP/3 removed the `Upgrade` mechanism
entirely, so their rejection is unconditional. Setting `1xx` as the final
`Response.StatusCode` therefore fails fast, and an interim write after the final
response has started is rejected by the capability — the two boundary criteria.

### Automatic `Expect: 100-continue` on HTTP/1.1 — lazy, at the first body read

A client that sends `Expect: 100-continue` withholds the body until it sees
`100 Continue`. With the request dispatched at head and the body streamed (see
"HTTP/1.1 request-body streaming and data rates"), the transport solicits the
body **lazily**: the parser computes the solicitation decision at head-parse time
(`ShouldSolicitContinue` — the expectation is declared and the framing indicates a
body; a `Content-Length: 0` or a CONNECT tunnel is not solicited), and
`Http1RequestBodyStream` emits `100 Continue` at the **first body read**, before
touching the wire, reusing the same `Http1MessageWriter.WriteInterimResponseAsync`
the interim-writer capability uses. The automatic handshake needs no feature
package registered — it is a wire-level interop concern the transport owns
unconditionally. A request without the expectation observes no interim response.

This is the lazy, application-driven model the buffered reader explicitly
de-scoped to the streaming-body rework (#810), and it is strictly stronger: a
handler can inspect the head and decline (`401` / `417`) **without the body ever
being solicited**. Three boundary rules keep the handshake legal:

- A Content-Length declaration over the effective body-size cap is rejected (413)
  **before** soliciting — an over-cap body is never invited onto the wire.
- Solicitation is suppressed once the final response has started (the body stream
  consults its owning exchange): an interim response must precede the final
  response (RFC 9110 §15.2). The read then proceeds unsolicited — a peer that
  transmits anyway (§10.1.1 permits it) is read normally; one that keeps waiting
  is reclaimed by the data-rate gate.
- A declared-but-never-solicited body cannot be **drained** for keep-alive: at
  drain time the final response is already on the wire, so `100 Continue` can no
  longer be sent, and whether the peer will transmit the body regardless is
  indeterminate. The connection closes instead of being reused
  (`Http1RequestBodyStream.DrainAsync` returns `false`).

### AOT posture

No reflection or runtime codegen. The interim-writer capabilities are small
per-exchange objects; the interim encoders are the existing HPACK/QPACK
field-section writers with a `1xx` `:status` and no `Content-Length`; the status
guards are integer range checks.

## HTTP/1.1 connection takeover (protocol upgrade / CONNECT)

### What it is

The takeover members of the exchange control
(`IHttpExchangeControl.CanTakeOver` / `TakeOver()`, implemented by
`Http1ExchangeControl` and surfaced on
`HttpExchangeInterceptorResponseContext.Control`). Exercising `TakeOver()` hands the
caller the **raw duplex connection stream** with no HTTP framing — the escape
hatch that RFC 9110 §7.8 protocol upgrades (`101 Switching Protocols`) and
§9.3.6 `CONNECT` tunnels need, since both transitions take the connection out
of the HTTP request/response loop entirely — from that instant the transport
has given up control of the exchange (its internal directive reads `TakeOver`).

### The layering (#751): all upgrade semantics live in `Http.ProtocolUpgrade`

This transport deliberately dropped its dependency on
`Assimalign.Cohesion.Http.ProtocolUpgrade` (commit `4c21d75`) and the bridge
was restored **without upgrade knowledge re-entering the transport**. The
transport does not detect upgrade signalling, does not know the
`context.Upgrade` surface, and installs no upgrade feature. It contributes
exactly three generic things:

- **The takeover members of the exchange control** (`Http1ExchangeControl`,
  offered per exchange alongside the framed sink whenever response-scoped
  interceptors are registered). `TakeOver()` is one-shot: it flips the exchange's
  `ResponseFinalized` flag (the internal directive reads `TakeOver`), clears `KeepAlive`, and
  returns the connection stream.
- **Response suppression**: `Http1ConnectionContext.SendAsync` no-ops when
  `ResponseFinalized` is set — checked *before* the streamed-sink branch so a
  misused streaming feature can never finalize chunked framing into a tunnel,
  and re-checked after the `BeforeResponseHead` hooks so a hook-driven takeover
  is honored before the head is written.
- **Keep-alive exit**: the receive loop already stops when `KeepAlive` is
  false, so no post-transition octet is ever parsed as a next request.

The `Http.ProtocolUpgrade` package owns everything else — detection over the
parsed head (via `IHttpExchangeInterceptor.AfterRequestHead`), the
`context.Upgrade` feature surface, the 101/200 accept path, and the
RFC-mandated framing-header scrub — wired by registering its single exchange
interceptor on the listener options (`HttpProtocolUpgrade.CreateInterceptor()`),
exactly how `Http.RequestLimits` and
`Http.Streaming` plug in. HTTP/2 / HTTP/3 controls report
`CanTakeOver == false` (their `TakeOver()` throws): their exchanges are
multiplexed streams over a shared connection, and those protocols removed the
`Upgrade` mechanism (their bootstrap is extended CONNECT, below).

### The no-over-read invariant

Handing over the raw stream is only safe because the HTTP/1.1 parser never
buffers past the request it parsed: the request line and headers are read
byte-by-byte, a CONNECT skips body framing entirely (RFC 9110 §9.3.6 — the
post-header octets belong to the tunnel), and a bodyless upgrade `GET` reads no
body. Octets a client pipelines behind the handshake therefore stay in the
connection stream and are readable from the surrendered stream. Preserving this
byte-exact read boundary is a hard constraint on any future read-path
optimization (read-ahead buffering would have to hand the remainder over with
the takeover).

### Unaccepted transitions

If no handler accepts, the exchange follows the normal path: the transport
writes the buffered response. For an unaccepted upgrade request that is exactly
right (RFC 9110 §7.8 — a server that does not switch protocols just answers the
request). For an unaccepted `CONNECT`, any pipelined tunnel octets will fail to
parse as a next request and the wire-failure classifier drops the connection —
unchanged from the pre-takeover behavior, and safe (the connection dies rather
than desynchronizing).

### AOT posture

No reflection or runtime codegen: the takeover is a two-flag flip plus a field
read, resolved through the existing interceptor seam.

## Receive-loop failure isolation

Each protocol's inbound processor (`Http1ConnectionContext.ReceiveAsync`, the
HTTP/2 background frame pump behind `Http2ConnectionContext.ReceiveAsync`, and
`Http3ConnectionContext.ReceiveAsync`) classifies failures into two scopes:

- **Per-connection wire-level failures** — truncated frames, malformed
  request lines, peer reset, socket I/O errors. The processor stops
  producing values and exits cleanly (the HTTP/2 pump completes the
  ready-context channel so the enumerable ends); the surrounding
  `await using` disposes the connection; the listener keeps accepting.
  Protocol-required wire frames (`GOAWAY` on HTTP/2 connection errors,
  `RST_STREAM` on HTTP/2 stream errors) are emitted before exit.
- **Per-stream failures** (HTTP/2, HTTP/3) — malformed headers on one
  stream, QPACK errors on one HTTP/3 stream. The processor emits `RST_STREAM`
  (HTTP/2) or drops the offending stream (HTTP/3) and continues
  processing subsequent streams on the same connection.

The design intent is *failure isolation*: a single malformed peer must
never bring down the listener. Cancellation propagates normally so
cooperative shutdown is unaffected.

Accept-loop failures sit outside this isolation model. If a transport
listener's `AcceptAsync` itself faults, the failure is fatal to the
`HttpConnectionListener`: the accept loop completes the backlog channel
with the listener's exception *before* cancelling the internal dispose
token (the ordering is load-bearing — a pending `AcceptOrListenAsync`
must observe the faulted channel, not the cancellation) and records the
exception so accepts that begin after cancellation rethrow it too. The
host therefore sees the transport's root-cause exception from
`AcceptOrListenAsync`, never a bare `ObjectDisposedException`.

## HTTP/1.1 server limits and timeouts

### Why this lives in the transport

An HTTP server that reads request bytes off a socket without bounding them is
trivially DoS-able. Two vectors are specific to the HTTP/1.1 read path and must
be closed *inside the transport*, before a request ever reaches the application:

- **Unbounded buffering (memory exhaustion).** `Http1MessageReader` reads the
  request line and each header line via a byte-at-a-time `ReadLineAsync` that
  accumulates into a `MemoryStream`. With no cap, a peer that opens a connection
  and streams an endless request line — or an endless run of header bytes with
  no terminating CRLF — grows that buffer without bound and exhausts the heap.
  This is a *live* memory-exhaustion vector, not a theoretical one.
- **Idle / slow peers (Slowloris).** The receive loop was previously bounded
  only by the ambient connection token. A peer that connects and then dribbles
  (or never sends) request bytes ties up a connection indefinitely; enough of
  them starve the server of connection slots.

Both are wire-level concerns the application layer cannot see (by the time a
context is dispatched the head is already parsed), so enforcement belongs here,
alongside the existing framing / smuggling defences.

### The limits surface

`Http1ConnectionListenerOptions.Limits` (`Http1Limits`, extending the shared
`HttpConnectionListenerLimits` base with the HTTP/1.1 wire-format bounds) is
the tuning surface, configured per registration through
`UseHttp1(listener, http1 => http1.Limits...)`, with conservative
Kestrel-`KestrelServerLimits`-parity defaults so a listener is protected out
of the box:

| Limit | Default | Enforced by | Rejection |
|---|---|---|---|
| `MaxRequestLineSize` | 8 KB | `Http1MessageReader` request-line read | `414` URI Too Long (RFC 9110 §15.5.15) |
| `MaxRequestHeaderCount` | 100 | header loop | `431` Request Header Fields Too Large (§15.5.22) |
| `MaxRequestHeadersTotalSize` | 32 KB | per-line cap = remaining budget | `431` |
| `MaxRequestBodySize` | ~28.6 MB (`null` = unbounded) | `Http1RequestBodyStream` (frozen at first read) | `413` Content Too Large (§15.5.14), on the body read |
| `MinRequestBodyDataRate` | 240 B/s, 5 s grace (`null` = off) | `Http1RequestBodyStream` | `408` Request Timeout (§15.5.9), on the body read |
| `MinResponseDataRate` | 240 B/s, 5 s grace (`null` = off) | `Http1ResponseBodyStream` (streaming sink) | exchange aborted (`IOException`) |
| `KeepAliveTimeout` | 130 s | `Http1ConnectionContext` | connection reclaimed |
| `RequestHeadersTimeout` | 30 s | `Http1ConnectionContext` | `408` Request Timeout (§15.5.9) |

The limits flow `UseHttp1` → `Http1ConnectionFactory` → `Http1Connection` →
`Http1ConnectionContext` → reader as a plain object reference, captured per
registration at `UseHttp1` time; there is no DI, config, or logging dependency
in this package (Lane A guardrail — config binding of these limits is a
Web.Hosting builder-time concern). The two data-rate limits carry an
`HttpMinDataRate` (octets/second + grace period); their enforcement is described
in "HTTP/1.1 request-body streaming and data rates" below.

**Head vs. body split.** The head limits (`414` / `431`) and the head-arrival
timeout are detected *before* the request is dispatched, so the transport emits a
clean bodyless status response and closes. The body-size (`413`) and
request-body data-rate (`408`) violations are detected *after* dispatch, on the
streamed body read (the request is dispatched at head — see below), so they
surface to the application as a read exception rather than a transport-written
status; the hosting layer's exception boundary maps them to a response if it has
not already started one.

### 414 / 431 / 413 semantics, not a silent drop

The pre-existing behaviour for a malformed request is to classify it as a
wire-level failure and drop the connection silently (the receive enumerable
yields nothing). For a *limit* violation that is user-hostile: a conformant
client gets no signal about why its connection died. So limit violations throw a
dedicated `Http1LimitExceededException` carrying the HTTP status to emit;
`Http1ConnectionContext.TryReadRequestAsync` catches it *before* the generic
wire-level catch, writes a minimal bodyless status response
(`Http1MessageWriter.WriteErrorResponseAsync` — status line + `Content-Length: 0`
+ `Connection: close`), and then ends the connection. The write is best-effort:
if the peer is already gone the I/O error is swallowed and the connection is
dropped anyway.

`Http1LimitExceededException` derives from `IOException` (not the sealed
`InvalidDataException`) precisely so that if it ever escapes the dedicated catch
it still degrades to the existing wire-level-drop path rather than faulting the
host — belt-and-suspenders on top of the explicit catch.

### The two-phase read timeout

`Http1ReadTimeout` reclaims idle and slow peers with a single
`CancellationTokenSource` (linked to the ambient connection token) whose deadline
moves through the request lifecycle:

1. **Keep-alive idle wait.** Armed with `KeepAliveTimeout` while the transport
   waits for the *first byte* of the next request. A connection that goes idle
   between requests (or never sends its first request) is reclaimed here — with
   no response, because there is no request to answer.
2. **Request-headers deadline.** The reader signals `OnRequestLineStarted` on the
   first request byte, which re-arms the CTS with `RequestHeadersTimeout`. This
   single deadline covers the entire head (request line + all header fields), so
   a Slowloris peer that dribbles headers is reclaimed — with a `408` because it
   is mid-request.
3. **Disarmed for the body.** After the blank line terminating the header
   section, the reader signals `OnHeadReceived`, which disables the timer so the
   body read is bounded only by the ambient connection token — and, when
   configured, by the `MinRequestBodyDataRate` gate the streamed body owns (see
   "HTTP/1.1 request-body streaming and data rates" below). The head timeout is a
   coarse deadline for a *bounded* head; the body is unbounded in size, so it
   needs an average-rate gate rather than a fixed deadline.

Every read on the connection stream uses `Http1ReadTimeout.Token`. When the
timer fires, `PipeReader.ReadAsync` throws `OperationCanceledException`; the
context distinguishes a *timeout* cancel from a *shutdown* cancel via
`TimedOut` (`this CTS fired && the connection token did not`) so cooperative
shutdown still propagates normally and is never mistaken for a Slowloris.

The read-timeout controller's token must **not** become the request's abort
token — the controller is disposed when the read completes, but the dispatched
context outlives it. The reader therefore threads the *connection* token
through to `Http1Context` as `requestAborted` and uses the controller's token
only for the reads it bounds.

### Per-request body-size override

The transport no longer seeds any feature itself. The per-request override
flows through the request-parse interceptor seam (see "Per-request feature
injection" above): the parser seeds `HttpExchangeInterceptorRequestContext
.MaxRequestBodySize` from `Limits.MaxRequestBodySize`, `AfterRequestHead` (and
`BeforeRequestBody`) hooks may adjust it, and the value is enforced against
whatever it holds when the body read begins (413 on violation). The typed
`IHttpMaxRequestBodySizeFeature` lives in the
`Assimalign.Cohesion.Http.RequestLimits` package, whose interceptor attaches a
write-through view over the context knob; this transport knows nothing about
it. Because the request is dispatched at head and the body streamed,
`Http1RequestBodyStream` freezes the knob at its **first read** — so the writable
window spans the parse-time hooks *and* every middleware / endpoint that runs
before the body is read. That is the point of the override: an endpoint that
legitimately accepts large uploads (or must not) can raise or lower its own cap
before consuming the body. See the next section for the freeze mechanics.

### Scope boundary

These limits cover the HTTP/1.1 read path only. HTTP/2 abuse limits
(rapid-reset, CONTINUATION flood, header-list size, SETTINGS/PING floods) are
governed by the frame machinery and live under
`Http2ConnectionListenerOptions.Limits` (`Http2Limits`) — see "HTTP/2 abuse
limits" below. HTTP/2 request-body buffering is bounded by flow-control
backpressure, documented in "HTTP/2 request-body flow control and
backpressure" below. `MaxConcurrentConnections` is an accept-loop concern
owned by the Web-runtime rewrite, not this surface.

### AOT posture

No reflection, no codegen. The limits are plain properties with guard-clause
validation; enforcement is byte counting and `CancellationTokenSource.CancelAfter`
timer arithmetic.

## HTTP/1.1 request-body streaming and data rates

### Why the request body streams now (dispatch-at-head)

The HTTP/1.1 read path historically read the **whole** request body into a
`byte[]` and wrapped it in a seekable `MemoryStream` *before* constructing the
context. That had three consequences the deferred half of #791 (this work, #810)
set out to fix:

- a client could force the server to buffer a full (cap-bounded) body in memory
  before the application saw a single byte;
- there was no place to enforce a **minimum body data rate** — the read was
  already complete by dispatch, so a slow-trickle sender was only bounded by the
  ambient connection token (an unbounded hang);
- the per-request body-size cap could only be adjusted by **head-hook
  interceptors**, never by **middleware / an endpoint**, because those run after
  dispatch and the cap was already frozen (and enforced) by then.

The read path now **dispatches the request at head** (like HTTP/2): the request
line and headers are parsed under the head limits, the framing is decided from
the headers, and the context is yielded with a **lazy** `Http1RequestBodyStream`
as `Request.Body`. No body octet is read until the application reads the stream.

### `Http1RequestBodyStream`

A forward-only, non-seekable `Stream` (the same shape as `Http2RequestBodyStream`)
that reads the body incrementally from the shared connection stream, applying the
framing decided up front (`Http1RequestBodyFraming`: none / Content-Length /
chunked). Load-bearing invariants:

- **Byte-exact, no read-ahead.** A Content-Length body reads at most the octets
  remaining; a chunked body reads its size lines, terminators, and trailer
  section one byte at a time. It never reads past the request's framing boundary,
  so octets a client pipelines behind this request (or behind an accepted upgrade
  / CONNECT handshake) stay in the connection stream for the next reader. This is
  the same no-over-read invariant the connection-takeover path already depends on.
- **Cap frozen at first read.** `EnsureStarted` (first read) freezes the parse
  context's body-size knob and resolves the cap. Up to that point head hooks
  *and* middleware may raise or lower it via `IHttpMaxRequestBodySizeFeature`. A
  Content-Length over the frozen cap is rejected before a byte is read; a chunked
  body is checked as it accumulates (413, as `Http1LimitExceededException`).
- **Trailers on completion.** For a chunked request the reader hands the stream a
  supported-but-empty `HttpTrailerCollection`; the stream fills that same
  collection from the trailer section when it reaches the terminating chunk. So
  `Request.Trailers` is populated only *after* the body is fully read — there is
  no "trailers ready" signal before then.
- **Disposal never touches the connection.** The stream does not own the
  connection stream, so `Dispose` only bars further public reads; it does not
  close or drain the connection.

### Keep-alive realignment (draining)

Because the application may leave the body unread (or partially read) — an early
`401`, a handler that ignores the body — a keep-alive connection would
desynchronize on the next request's framing. So after each exchange
`Http1ConnectionContext.ReceiveAsync` calls `context.DrainRequestBodyAsync`
before reading the next request: it consumes and discards any unread body under
the same cap and data-rate enforcement. A drain that cannot complete cleanly
(slow trickle, over-cap, malformed body, wire failure) returns `false` and the
connection is closed instead of reused. Draining works after the body stream has
been disposed (it operates on the connection stream, not the disposed wrapper).

### The data-rate gate (`MinDataRateGate`)

`MinRequestBodyDataRate` and `MinResponseDataRate` (both `HttpMinDataRate` =
octets/second + grace period, Kestrel-parity 240 B/s over 5 s) are enforced by a
shared `MinDataRateGate`. The gate is **pure accounting**: it owns no timer and
does no I/O. The invariant is *cumulative wait ≤ grace + bytes / rate* — the
allowance grows with every octet the peer delivers, so a peer that bursts earns
proportional slack while one that stalls exhausts it. Two design choices matter:

- **Only peer-wait time counts.** The gate records only the duration of the
  blocking transport operation (the socket read / write), never the time the
  application spends between operations. A slow application consuming a healthy
  body — or a slow handler producing a response — never trips the rate; only a
  slow *peer* does.
- **`TimeProvider`, not `DateTime`.** All timing is `TimeProvider.GetTimestamp`
  ticks, so measurement and the caller's cancellation deadline
  (`new CancellationTokenSource(delay, timeProvider)`) share one monotonic clock,
  and the gate's arithmetic is unit-testable with a fixed-frequency fake clock.
  `TimeProvider.System` is used in production (threaded from
  `Http1ConnectionContext`); AOT-safe, no reflection.

On the request side, a read that would exceed the allowance is failed with an
`Http1LimitExceededException(408)`; on the response side (the streaming sink,
`Http1ResponseBodyStream`), a write / flush that blocks too long on a slow reader
is failed with an `IOException` (the response has already started, so its status
cannot change — the exchange is aborted as a wire failure). Both are `IOException`
subtypes, so an unhandled data-rate failure degrades to the wire-level-drop path
rather than faulting the host.

### AOT posture

No reflection, no runtime codegen. The body stream is a plain async framing
decoder; the gate is integer/`double` arithmetic over `TimeProvider` ticks; the
per-operation deadline is a linked `CancellationTokenSource` constructed with the
injected `TimeProvider`.

## HTTP/2 abuse limits

### Why the frame machinery isn't enough

The HTTP/2 transport already enforces the RFC 9113 structural caps —
`SETTINGS_MAX_CONCURRENT_STREAMS`, `MAX_FRAME_SIZE`, the connection/stream
flow-control windows, and the HPACK dynamic-table bound. None of those defend
against the *known HTTP/2 abuse classes*, where a client sends a trivially cheap
message that forces the server into unbounded (or amplified) work:

- **Rapid reset (CVE-2023-44487).** A client opens a stream and immediately
  `RST_STREAM`s it. Each cycle costs the client one HEADERS + one RST_STREAM but
  makes the server allocate, dispatch, and tear down a stream — and because the
  stream is closed, it never counts against `MAX_CONCURRENT_STREAMS`. Unbounded,
  this is a CPU/allocation DoS.
- **CONTINUATION flood.** A HEADERS frame without `END_HEADERS` followed by an
  endless run of `CONTINUATION` frames grew an *unbounded* `MemoryStream` — the
  header block was accumulated with no cap at all. (The request *body* is bounded
  by flow control — see the flow-control section below — but header blocks sit
  outside flow control per RFC 9113 §6.9, so they need their own bound.)
- **Oversized decoded header list.** HPACK amplifies: a few indexed references
  can expand into a huge decoded field list. The decode path had no
  `MAX_HEADER_LIST_SIZE` enforcement (it was advertised but never checked on the
  way in), so a small encoded block could materialise a large list.
- **SETTINGS / PING floods.** Each inbound SETTINGS frame forces a re-parse and
  an ACK; each inbound PING forces a PING ACK (an amplification vector). Neither
  was rate-limited.

### The limits surface

`Http2ConnectionListenerOptions.Limits` (`Http2Limits`, extending the shared
`HttpConnectionListenerLimits` base) is the operator-tunable surface,
configured per registration through `UseHttp2(listener, http2 =>
http2.Limits...)`, with conservative Kestrel-`Http2Limits`-parity defaults so
a listener is protected out of the box:

| Limit | Default | Enforced by | Vector |
|---|---|---|---|
| `MaxStreamsPerConnection` | 100 | advertised `SETTINGS_MAX_CONCURRENT_STREAMS`; `OpenInboundStream` refuses excess with `RST_STREAM(REFUSED_STREAM)` | concurrency exhaustion |
| `MaxRequestHeaderListSize` | 16 KB | advertised `SETTINGS_MAX_HEADER_LIST_SIZE`; raw-byte cap in `Http2Stream.AppendHeaderBytes`; decoded-size cap in `HPackDecoder` | CONTINUATION flood + header-list amplification |
| `MaxResetStreamsPerWindow` | 200 | `ProcessRstStreamFrameAsync` via `Http2FloodGuard` | rapid reset (CVE-2023-44487) |
| `MaxSettingsFramesPerWindow` | 100 | `ProcessSettingsFrameAsync` via `Http2FloodGuard` | SETTINGS flood |
| `MaxPingFramesPerWindow` | 100 | `ProcessPingFrameAsync` via `Http2FloodGuard` | PING flood |
| `FloodDetectionWindow` | 5 s | the trailing window shared by the three flood counters | — |

The limits flow `UseHttp2` → `Http2ConnectionFactory` → `Http2Connection` →
`Http2ConnectionContext` as a plain object reference, captured per
registration at `UseHttp2` time; there is no DI, config, or logging dependency
in this package (Lane A guardrail — config binding is a Web.Hosting
builder-time concern), exactly as with the HTTP/1.1 `Http1Limits`.

### Escalation: GOAWAY(ENHANCE_YOUR_CALM), mirroring Kestrel

Every abuse trip escalates the same way Kestrel does: a connection-level
`Http2ConnectionException(ENHANCE_YOUR_CALM)` (RFC 9113 §7, error code `0x0b`),
which the frame pump's existing failure-isolation path turns into a `GOAWAY`
carrying that code before the connection tears down. `ENHANCE_YOUR_CALM` is the
RFC's "your peer is generating excessive load" signal, so a well-behaved client
learns to back off and can retry its in-flight streams on a fresh connection.
The **frame-size** violation (PING length ≠ 8) is the one exception — it is a
structural fault, so it escalates as `FRAME_SIZE_ERROR` per RFC 9113 §6.7.

### The two header-list caps are complementary, not redundant

`MaxRequestHeaderListSize` bounds *two different things* because encoded and
decoded sizes diverge:

- **Raw accumulation** (`Http2Stream.AppendHeaderBytes`) bounds the on-the-wire
  header-block bytes across a HEADERS frame and all its CONTINUATION frames. It
  trips *before* HPACK decode, so a CONTINUATION flood can never pin memory in
  the `MemoryStream` — the buffer is capped, replacing the previously-unbounded
  growth. This is the CONTINUATION-flood defence.
- **Decoded size** (`HPackDecoder.DecodeRequestHeaders`) bounds the RFC 9113
  §10.5.1 header-list size — the running sum of `name-length + value-length + 32`
  across the fields. This catches HPACK *amplification*: a small encoded block of
  indexed references whose decoded list is large. The decode aborts the moment
  the running total exceeds the cap, so the oversized list is never fully
  materialised.

Because the decode aborts early, the HPACK dynamic-table state is left
indeterminate, so the overflow is a **connection** error (GOAWAY), not a
recoverable stream reset — the decoder is connection-global and cannot be
trusted for subsequent streams once a decode was abandoned mid-block. The
distinct `HPackHeaderListSizeExceededException` (a subclass of
`HPackDecodingException`) lets `TryDispatchStream` map the size overflow to
`ENHANCE_YOUR_CALM` while genuinely malformed field sections keep mapping to
`PROTOCOL_ERROR`.

### The flood detectors are sliding windows

`Http2FloodGuard` owns three `Http2SlidingWindowCounter`s (reset / SETTINGS /
PING), one per rate-limited frame class, all sharing `FloodDetectionWindow`.
Each counter records event timestamps (`Environment.TickCount64`, monotonic and
allocation-free) and, on every event, evicts timestamps older than
`now - window` before comparing the in-window count to the maximum. The window
is a genuine *sliding* window rather than a tumbling one, so a burst straddling a
fixed bucket boundary cannot slip past the limit; the backing `Queue<long>` only
ever holds the events currently inside the window (at most `max + 1` before the
trip), so memory tracks live traffic, not the configured maximum. The guard is
driven solely from the frame pump — the connection's single inbound frame
processor — so it needs no synchronization.

The rapid-reset counter counts only inbound `RST_STREAM`s that target a stream
the server has actually opened (or recently retired). Two deliberate
exclusions keep the accounting honest:

- A `RST_STREAM` on a never-opened (idle) stream is a *different* violation —
  the RFC 9113 §6.4 `PROTOCOL_ERROR` handled just below — and is excluded so
  the two failure modes stay distinct.
- The server's **own** `RST_STREAM(NO_ERROR)` emissions (the routine
  undrained-body reset on the response path) never pass through
  `ProcessRstStreamFrameAsync`, so ordinary server operation cannot trip the
  peer-abuse detector.

### PING validation

`ProcessPingFrameAsync` now also enforces the two RFC 9113 §6.7 structural rules
that were previously unchecked: a PING payload length other than 8 octets is a
`FRAME_SIZE_ERROR` (checked ahead of the ACK short-circuit so a malformed ACK is
rejected too), and a PING on any stream other than 0 is a `PROTOCOL_ERROR`.

### Scope boundary — HTTP/2 only, not HTTP/3

These are HTTP/2 frame-machinery limits. HTTP/3's equivalent stream-churn and
flow-control limits live in the QUIC transport (`MAX_STREAMS`, QUIC flow
control), not here — deliberately, per the guardrail that h3 stream limits are a
QUIC-transport concern. HTTP/2 request-body buffering is bounded by the
flow-control backpressure documented in the next section; the two surfaces are
complementary (frame-rate abuse here, byte-volume abuse there).

### AOT posture

No reflection, no runtime codegen. The limits are plain properties with
guard-clause validation; the flood detectors are queue arithmetic over
`Environment.TickCount64`; the header-list caps are byte counting and a running
integer sum during decode.

## HTTP/2 request-body flow control and backpressure

### The vulnerability this closes

HTTP/2 (RFC 9113 §5.2) makes flow control **receiver-driven**: each receiver
advertises a per-stream window (`SETTINGS_INITIAL_WINDOW_SIZE`, 65535 octets by
default) and a fixed connection window (also 65535), and the sender may only
transmit that many DATA octets before the receiver credits more capacity with a
`WINDOW_UPDATE`. The receiver paces the sender by choosing *when* to credit.

The previous implementation defeated that mechanism twice over. It credited the
window **immediately on receipt** — the receive loop emitted `WINDOW_UPDATE` for
every DATA frame as soon as it was parsed — and it **buffered the whole body**
before the request was dispatched (`Http2Stream.CreateContext` ran only once the
stream was complete, materializing a `MemoryStream` over the accumulated bytes).
Together those meant a client could stream a body of any size as fast as it
liked and the server would buffer all of it in memory before the application saw
a single byte. HTTP/1.1 has a body cap (`Http1MessageBodyReader`); HTTP/2 had
none. This is the memory-exhaustion DoS #750 closes.

### The shape: a decoupled frame pump feeding per-stream body pipes

Real end-to-end backpressure is impossible while frame reading is driven by the
request-consumption loop: the server dispatches contexts sequentially (a handler
runs to completion before the next context is pulled), so nothing would drain
the wire while a slow handler read its body — the sender could not be paced and,
worse, a body larger than the window could never complete, deadlocking the
handler. So inbound processing is now owned by a **single background frame pump**
(`Http2ConnectionContext.PumpAsync`), started once when `ReceiveAsync` is first
enumerated and decoupled from how fast the consumer handles requests:

- The pump reads and processes **every** inbound frame for the connection's
  lifetime. It dispatches a request head to a ready-context channel as soon as
  the header block is complete (RFC 9113 lets the server respond before the body
  arrives), then keeps pumping that stream's DATA into a per-stream body pipe
  (`Http2Stream`'s unbounded `Channel<Http2DataChunk>`) while the handler runs.
- `ReceiveAsync` simply yields ready contexts off that channel. `SendAsync` and
  the request-body reads run concurrently with the pump.
- The application reads the body through `Http2RequestBodyStream`, which drains
  the channel and — this is the whole point — credits each fully-consumed chunk's
  flow-control cost back to the peer via `OnRequestBodyConsumedAsync`. **Credit is
  driven by consumption, not receipt.**

### Why this bounds buffering and preserves FLOW_CONTROL_ERROR enforcement

Because the pump consumes the receive window as DATA arrives but only credits it
back as the application reads, the unconsumed, buffered bytes for a stream can
never exceed the advertised window: a conformant sender that fills the window
stalls until the reader drains the pipe. A misbehaving sender that transmits more
than the window without waiting is still caught — the pump consumes the receive
window eagerly on receipt (`ProcessDataFrameAsync`), so an overshoot fails
`TryConsume` and raises `FLOW_CONTROL_ERROR` exactly as before (connection-level
on the shared window, stream-level on the per-stream window, RFC 9113 §6.9.1).
Eager consumption is *load-bearing* for that check: a lazy, read-only-when-asked
design would pace the window in lockstep with the reader and never detect the
overshoot at all.

Flow control accounts for the **entire DATA frame payload including padding and
the pad-length octet** (RFC 9113 §6.9.1), while only the de-padded data reaches
the application. The two lengths differ for padded frames, so each
`Http2DataChunk` carries its `FlowControlLength` (the full payload length)
independently of its data, and the credit emitted on consumption is the flow-
control length, not the byte count the reader saw. A padded frame whose pad
length meets or exceeds the payload is a `PROTOCOL_ERROR` (RFC 9113 §6.1),
rejected before it is queued.

Two window-conservation paths keep the shared connection receive window from
leaking under consumption-driven credit — critical, because unlike the old
credit-on-receipt model an un-drained body would otherwise never return its
octets to the peer:

- **Recently-closed discard.** DATA that arrives for a stream we have already
  retired is discarded, but its connection-window cost is credited back
  (RFC 9113 §6.9) before the stream error is raised, so a benign close race
  does not shrink the window.
- **Removal reclaim.** When a stream is removed while buffered body sits
  unconsumed (an ignored body, a reset, an abandoned upload), its outstanding
  receive debt — exactly `InitialReceiveWindow - ReceiveWindow.Available` — is
  credited back to the connection window and a connection-level `WINDOW_UPDATE`
  is emitted. Without this, every request whose handler skips the body (an
  auth-rejected `POST`, an early 4xx) would permanently shrink the connection
  window until inbound DATA stalled on every stream. A per-stream
  `ReceiveReclaimed` flag makes reclaim and the reader's consumption-credit
  mutually exclusive so an octet is never credited twice.

### Concurrency model

The pump is the single inbound processor, so pump-only state (remote settings,
send windows, the continuation-tracking id) needs no synchronization. Two locks
guard the state the pump and the application threads genuinely share, and neither
is ever held across an await or a wire read:

- A connection-level `_syncRoot` guards the stream table and the **receive**-
  direction windows (the pump consumes them; the body reads credit them).
- A per-`Http2Stream` `_stateLock` guards lifecycle transitions, because the pump
  drives the remote half (`Receive*`) while the handler drives the local half
  (`SendEndStream` / a local reset) concurrently. Modeling `State` as a single
  enum keeps the two halves from racing to a lost update.

All outbound frames — response HEADERS/DATA, SETTINGS/PING ACKs, GOAWAY, and both
receipt-side and consumption-side `WINDOW_UPDATE`s — serialize through the
connection write gate (`Http2WriteScheduler`), so nothing tears a frame sequence
(RFC 9113 §4.1). The gate additionally grants contending writers in RFC 9218 §10
priority order — see the extensible-priorities section below.

### Lifecycle: dispatch-at-headers, abandoned bodies, teardown

The request head is dispatched once, when the header block completes; the body
streams in afterward. A handler that responds **without draining the request
body** would otherwise leave the stream in `HalfClosedLocal` forever, eventually
exhausting `SETTINGS_MAX_CONCURRENT_STREAMS`, so after `SendAsync` writes the
response the server emits `RST_STREAM(NO_ERROR)` to tell the peer to stop and to
reclaim the stream slot (RFC 9113 §8.1). On connection teardown, wire failure, or
a connection error, the pump's `finally` aborts every live stream and then
completes the ready-context channel: a stream whose body was **still incoming**
fires its `RequestAborted` so a handler parked reading it observes cancellation
rather than a clean end-of-stream (which would let it treat a truncated upload as
complete), while a **fully-received** body stays readable to completion. Aborting
before completing the channel makes the abort observable to a consumer that is
about to see the enumerable end. Graceful close emits the shutdown GOAWAY while
the pump is still running — the write scheduler serializes it against the pump's
writes — then drains in-flight exchanges (bounded) and only then cancels and
awaits the pump (see "HTTP/2 graceful close").

### AOT posture

No reflection, no runtime code generation. The pump is a plain async loop, the
body pipe is a `System.Threading.Channels` channel, and the flow-control windows
are value-type octet counters guarded by monitors.

### Non-goals

- **Outbound (response) flow control.** `SendAsync` does not yet consult the
  per-stream send window; a streaming response write path (with send-side
  backpressure and SSE) is tracked separately (#769). Response bodies are still
  buffered before framing.
- **A configurable initial window.** The advertised
  `SETTINGS_INITIAL_WINDOW_SIZE` is the fixed RFC default (65535). Exposing a
  tunable stream/connection window (Kestrel-style) is a later refinement.

## HTTP/2 graceful close (GOAWAY + stream drain)

RFC 9113 §6.8 makes an orderly HTTP/2 shutdown a two-part gesture: announce
the close with `GOAWAY(NO_ERROR)`, then let the streams already accepted
finish before the wire goes away. `Http2ConnectionContext.GracefulCloseAsync`
performs both, in order:

1. **Refuse new streams.** Setting `_gracefulCloseStarted` (an interlocked
   one-shot) makes `OpenInboundStream` answer any HEADERS opening a *new*
   stream with `RST_STREAM(REFUSED_STREAM)`. The connection stays alive and
   keeps draining; the client may retry the refused request on a fresh
   connection (RFC 9113 §8.1.4). Refused streams never bump the observed
   last-stream-id, which is what makes the GOAWAY snapshot below exact.
2. **Emit `GOAWAY(NO_ERROR)`** carrying the highest inbound stream ID
   observed (snapshotted under the stream-table lock — the pump is still
   processing frames concurrently), so the peer learns exactly which streams
   will still be processed.
3. **Drain, bounded.** `DrainActiveExchangesAsync` waits for every request
   already dispatched to the application to finish — its response sent, its
   stream reset, or its truncated request shutdown-aborted — before teardown
   proceeds. Without this wait the previous behavior completed
   `connection.Output` immediately, cutting off a response a handler was
   still writing. The wait is capped by `GracefulDrainWindow` (a fixed
   internal ceiling, not host-configurable): a stuck or slow response cannot
   delay teardown indefinitely, and when the window elapses the remaining
   exchanges are abandoned.
4. **Stop the pump, then complete the output** so the transport's send loop
   flushes its backlog and closes the socket (the #686 ordering).

The pump deliberately keeps running through the drain window — this is what
distinguishes the drain from a passive sleep. A live pump is what refuses
newly opened streams *on the wire*, keeps feeding in-flight request bodies,
replenishes send-window credit so a streaming response writer parked on flow
control can finish, and observes peer `RST_STREAM`s that release the drain
early. GOAWAY-vs-pump write interleaving is prevented by the connection
write scheduler (control frames use `ControlUrgency`), not by stopping the
reader. If the pump exits on its own during the drain (peer end-of-stream or
wire failure), its shutdown abort releases the accounting of **truncated**
requests only — a fully-received request stays counted because its handler
can still respond through the pump-independent write path, and the drain
exists precisely to give it that chance.

The drain tracks an interlocked `_activeExchangeCount` rather than reading
the `_streams` table from the close thread. A stream is counted by the pump
(`MarkExchangeCounted`, immediately before its context is handed to the
consumer) and released exactly once through the stream's tri-state
accounting latch (`TryClaimExchangeAccounting`) — every exchange-terminating
path funnels through `RemoveStreamAsync` (response sent on the buffered or
streaming path, local reset, peer reset), with the pump's shutdown abort
covering truncated requests. When the count reaches zero a draining close is
woken through a published `TaskCompletionSource` (no polling); a re-check
after publishing the signal closes the lost-wakeup window. When nothing is
in flight the whole step is skipped.

> The **host-facing** variant — a "drain now, close later" trigger the host
> calls before dispose, rather than draining inside dispose — is a separate
> public-surface decision (interface-first, implementation internal) and is
> deliberately out of scope here. This section covers only the
> drain-inside-`GracefulCloseAsync` behavior. The optional RFC 9113 §6.8
> dual-`GOAWAY` pattern (a first `GOAWAY` at max stream ID, a second at the
> true last-stream-ID after draining) is likewise not implemented; the
> single `GOAWAY` carrying the real last-stream-ID is sufficient and simpler.

## HTTP/3 stream model and SETTINGS engine

### What it is

HTTP/3 (RFC 9114) runs over QUIC, which surfaces two kinds of
peer-initiated streams: **bidirectional** streams carry requests, and
**unidirectional** streams carry control data, QPACK table
synchronisation, and (from a server) pushes. RFC 9114 §6.2.1 also requires
each peer to open **its own** unidirectional control stream and send
SETTINGS first, so `Http3ConnectionContext` both emits an outbound control
stream and demultiplexes inbound streams off a single accept loop:

```
on receive start → open outbound control stream (WriteOnly):
     write stream-type 0x00 + SETTINGS frame, keep open (critical stream)

accept inbound QUIC stream
  ├─ bidirectional → request stream → parse HEADERS/DATA → yield IHttpContext
  └─ unidirectional → read stream-type varint (RFC 9114 §6.2):
       0x00 control      → read+apply SETTINGS, then drain later frames
       0x02 QPACK encoder→ accept (no instructions; dynamic table disabled)
       0x03 QPACK decoder→ accept (no instructions; dynamic table disabled)
       0x01 push         → connection error (client must not push)
       other             → abandon (unknown types are not an error)
```

The stream direction is reported by the transport via
`IConnection.Direction` on each accepted stream (see below); the HTTP
layer never inspects QUIC stream IDs directly.

### The server control stream and SETTINGS emission

RFC 9114 §6.2.1 requires **each** peer — including the server — to open a
unidirectional control stream and send SETTINGS as its first frame. At the
start of the receive loop the engine opens one outbound stream via
`IMultiplexedConnection.OpenStreamAsync(ConnectionDirection.WriteOnly)`,
writes the stream-type varint `0x00` (control) followed by a SETTINGS frame,
and then **leaves the stream open** for the connection lifetime. The frame
is written straight to the stream's `Output` `PipeWriter` (whose `WriteAsync`
flushes), the symmetric counterpart to reading inbound control frames off
`Input`; the outbound `Output` is never completed while the connection
serves requests.

`Http3LocalSettings.EncodePayload` serialises the advertised payload — the
same posture as the HTTP/2 transport's initial SETTINGS:

- **`SETTINGS_ENABLE_CONNECT_PROTOCOL` (0x08) = 1** (RFC 9220 §3) tells peers
  they may initiate extended CONNECT (`CONNECT` + `:protocol`) over HTTP/3,
  matching the HTTP/2 transport's RFC 8441/9220 stance. This is what
  unblocks WebSocket-over-HTTP/3 clients, which will not send an extended
  CONNECT until the server advertises the capability.
- **`QPACK_MAX_TABLE_CAPACITY` (0x01)** (RFC 9204 §5) is the server's decoder
  capacity. It defaults to `0` — stating explicitly that the QPACK dynamic
  table is disabled (see the QPACK section) — and is raised to the configured
  `Http3QPackOptions.MaxTableCapacity` when the dynamic table is opted in, in
  which case **`QPACK_BLOCKED_STREAMS` (0x07)** is advertised alongside it.

Emission is best-effort: opening an outbound stream requires a live QUIC
connection, so if the connection is already gone the setup failure is
swallowed and the accept loop terminates on the same underlying failure —
the exception never surfaces into the consumer's enumeration.

Setting **identifiers** are defined once in `Http3SettingId` (the shared
wire registry) and referenced by both `Http3LocalSettings` (what the server
sends) and `Http3PeerSettings` (what the peer sent), so the two directions
never duplicate the identifier literals.

> Beyond the extended-CONNECT enabler, emitting SETTINGS closes an RFC 9114
> §6.2.1 conformance gap: a server that sent no SETTINGS at all could be
> failed by a strict client with `H3_MISSING_SETTINGS`.

### The peer control stream and SETTINGS

RFC 9114 §6.2.1 / §7.2.4 impose two hard rules the engine enforces on the
**peer's** control stream as connection errors (the loop stops yielding and
the connection tears down):

- **At most one control stream per peer.** A second control stream is
  `H3_STREAM_CREATION_ERROR`.
- **The first frame on the control stream MUST be SETTINGS.** A missing
  or non-SETTINGS first frame is `H3_MISSING_SETTINGS`.

The SETTINGS payload is parsed into `Http3PeerSettings`, a small
identifier→value store keyed by the `Http3SettingId` registry. Unknown
identifiers are retained-but-ignored per RFC 9114 §7.2.4.1. The opening
SETTINGS frame is read and applied synchronously (so a missing/non-SETTINGS
first frame terminates the connection inline); the stream is then handed to
a **background drain** (`DrainPeerControlStreamAsync`) that parses and
discards subsequent control frames for the connection lifetime. Draining on
a background task is load-bearing: the control stream is long-lived, so
draining it inline would block the accept loop from ever serving another
request. Post-SETTINGS frames are read but inert in this subset — a peer
`GOAWAY` (§7.2.6) is discarded rather than acted on (the server does not
implement the *client* role of graceful shutdown — reacting to a peer's
`GOAWAY` — only the server role of *emitting* one, see "Graceful GOAWAY on
the control stream"), and `MAX_PUSH_ID` (§7.2.7) is discarded because the
server never pushes. The drain exists so those frames cannot accumulate
unread in the pipe; it stops on end-of-stream, connection teardown, or a
per-stream parse failure and never throws into the receive loop.

### QPACK encoder/decoder streams

Each of the QPACK encoder (0x02) and decoder (0x03) streams may appear
at most once (RFC 9204 §4.2); a duplicate is a connection error. With
the QPACK dynamic table disabled (`QPACK_MAX_TABLE_CAPACITY = 0`, the
default posture) these streams carry no instructions the server must act
on, so accepting the stream and recording that it was seen is sufficient.

When the dynamic table is **enabled** (opt-in, see "Dynamic table" below),
the peer's encoder stream is drained on a background task
(`DrainQPackEncoderStreamAsync`) that applies each Set Capacity / Insert /
Duplicate instruction to the shared decoder table and emits an Insert Count
Increment on the server's own decoder stream (which the server opens at
receive start, symmetric to its control stream). Both server-opened streams
are critical streams — left open for the connection lifetime and released by
the connection-first teardown, never completed early.

### Push streams

A client opening a push stream (type 0x01) is `H3_STREAM_CREATION_ERROR`
— only a server may push, and Cohesion does not push (see "server push
(de-scoped)" below). The engine treats it as a connection error.

### Connection teardown — critical streams and close ordering

Three long-lived unidirectional streams stay open for the connection's
lifetime: the server's **own outbound control stream**, and the accepted
**peer control and QPACK** streams. RFC 9114 §6.2.1 and RFC 9204 §4.2 make
them all *critical* streams — a peer that observes one of them terminate
(FIN, RESET, or a STOP_SENDING request) before the connection close MUST
fail the whole connection with `H3_CLOSED_CRITICAL_STREAM`. Teardown is
therefore connection-first: `Http3Connection.DisposeAsync` first emits the
graceful-shutdown `GOAWAY` on the outbound control stream (see "Graceful
GOAWAY on the control stream" below), then delegates to the multiplexed
connection, whose dispose completes bidirectional (request) streams —
delivering any in-flight response data — then closes the QUIC connection
(`CONNECTION_CLOSE` with the transport's configured close code,
`H3_NO_ERROR` by default on the QUIC driver's options), and only then
releases the unidirectional streams locally, after the close means no
stream-level frames can reach the peer. The `CONNECTION_CLOSE` ordering and
critical-stream close discipline live in the QUIC driver
(`QuicMultiplexedConnection`), not here: any multiplexed protocol with
long-lived unidirectional control channels needs the same discipline. Only
the `GOAWAY` emission — an HTTP/3 concern — is added ahead of it in this
layer.

The context's own teardown (`ShutdownAsync`, run from the receive loop's
`finally`) is deliberately minimal: it cancels the inbound control-stream
drain and awaits it, but **never completes, aborts, or FINs the outbound
control stream**. Completing it early — before the connection close — is
exactly the `H3_CLOSED_CRITICAL_STREAM` violation the connection-first
ordering exists to avoid, so the context leaves the outbound critical stream
for the multiplexed connection's dispose to release alongside the close. The
`GOAWAY` written during dispose rides on that still-open critical stream and
does not complete it.

### Graceful GOAWAY on the control stream

RFC 9114 §5.2 shuts a connection down by sending `GOAWAY` on the control
stream ahead of the `CONNECTION_CLOSE`. Its payload is a single QUIC
variable-length integer (RFC 9114 §5.2 / §7.2.6) — for a server, the
client-initiated bidirectional stream ID that marks the processing
boundary: requests on streams **below** the announced value may have been
processed and are allowed to finish; requests at or above it are not
processed and the client may safely retry them elsewhere.

`Http3GoAwayFrame.Encode` serializes that frame (type `0x07`, a length
prefix, then the varint stream ID) as pure buffer arithmetic. The boundary
value is derived from `_processedRequestStreamCount` — the number of
bidirectional request streams the receive loop has accepted — using QUIC's
client-bidi numbering (ID = `4 × n`), so after *k* accepted streams the
announced ID is `4 × k`: the *k* accepted streams (IDs `0 … 4(k-1)`) fall
below the boundary and may complete, while `4k` and above are rejected. The
count advances at *accept*, not at dispatch, so a malformed stream the
server touched and dropped still falls inside "may have been processed" and
the client will not retry a request whose side effects may have run. The
connection abstraction surfaces only an opaque `ConnectionId`, not the
numeric QUIC stream ID, so this count-based derivation is how the HTTP/3
layer reconstructs the boundary.

`SendGoAwayAsync` writes the frame to the retained outbound control stream
and is best-effort and one-shot: if the receive loop never ran (no control
stream, no advertised SETTINGS) there is nothing to announce and it is a
no-op; a wire/QUIC failure while writing is swallowed because the
`CONNECTION_CLOSE` that follows conveys the shutdown regardless.

### Incremental reads off the PipeReader

The unidirectional-stream handlers read directly off the accepted
stream connection's `PipeReader` (`IConnection.Input`) using a buffered
`ReadOnlySequence<byte>` model, **not** the `AsStream()` adapter that
the request path uses. Two reasons:

1. **Correct incremental framing.** Control data arrives as a varint
   stream-type prefix followed by length-delimited frames. A varint's
   width is encoded in its first two bits, so the reader must be able to
   buffer "not enough bytes yet, ask for more" without losing the bytes
   it already saw. `PipeReader.AdvanceTo(consumed, examined)` expresses
   exactly that; layering a `Stream` over the pipe and reading
   byte-by-byte does not, and in practice the adapter reported spurious
   end-of-stream when a multi-byte read followed a run of single-byte
   varint reads on the same pipe.
2. **No double-buffering.** Reading the sequence in place and slicing the
   SETTINGS payload out of the buffered segment avoids copying the whole
   stream into a `MemoryStream` first.

`QuicVariableLengthInteger.TryDecode(ReadOnlySequence<byte>, …)` is the
incremental counterpart to the existing span-based `Decode`; it reports
how many bytes it consumed so the loop can advance the reader precisely.

> **Latent decoder bug fixed in passing.** The QUIC varint length
> selector was written `first >> 6 switch { … }`. The C# `switch`
> expression binds tighter than `>>`, so this parsed as
> `first >> (6 switch { … })` = `first >> 8` — always `0` for a single
> byte, meaning *every* varint was decoded as one byte. Single-byte
> values (< 64) decode correctly that way, which is why no prior test
> caught it; the first multi-byte varint on the decode path (a SETTINGS
> value of 8192) exposed it. Fixed to `(first >> 6) switch { … }` in
> `Decode`, `ReadAsync`, and `TryDecode`.

### Why stream direction lives on the connection abstraction

Demultiplexing request streams from control/QPACK/push streams requires
knowing a stream's direction, and only the transport knows it. The
signal is `IConnection.Direction` in `Assimalign.Cohesion.Connections`:
stream transports always report `Bidirectional`, while a multiplexed
transport's unidirectional streams report `ReadOnly` (inbound; the
output throws) or `WriteOnly` (outbound; the input is pre-completed).
The HTTP/3 engine checks
`streamConnection.Direction == ConnectionDirection.Bidirectional` to
route request streams and treats everything else as a typed
unidirectional stream. Keeping the signal on the connection contract —
rather than inferring direction in the HTTP layer — preserves the
dependency direction and lets any future protocol over QUIC reuse it.

### AOT posture

No reflection, no runtime code generation. Stream-type dispatch is a
`switch` over varint constants; SETTINGS parsing is buffer arithmetic;
the peer-settings store is a plain dictionary.

### Non-goals

- **Acting on a peer's post-SETTINGS control frames.** A peer `GOAWAY`
  and `MAX_PUSH_ID` are *drained* (parsed and discarded by the background
  control-stream drain, so they cannot accumulate unread) but not *acted
  on*: the server never pushes, so `MAX_PUSH_ID` is inert, and reacting to
  a peer's `GOAWAY` (the client role of graceful shutdown) is future work.
  Note this is distinct from *emitting* the server's own `GOAWAY`, which
  now ships — see "Graceful GOAWAY on the control stream".
- **Emitting server `MAX_PUSH_ID`.** The server control stream carries its
  opening SETTINGS frame and, at teardown, a graceful-shutdown `GOAWAY`
  (RFC 9114 §5.2). `MAX_PUSH_ID` is never emitted because the server never
  pushes.
- **QPACK dynamic table (when disabled).** With the default
  `QPACK_MAX_TABLE_CAPACITY = 0`, the encoder/decoder streams are accepted but
  not processed and field sections resolve against the static table only. The
  opt-in dynamic table (encoder-stream drain, decoder stream, blocked-stream
  bookkeeping) is described under "QPACK field-section compression → Dynamic
  table (opt-in)".
- **Flow control / stream limits.** QUIC-level flow control and
  `MAX_STREAMS` accounting live in the QUIC transport, not here.

## QPACK field-section compression

### What it is

HTTP/3 carries header and trailer fields as QPACK-compressed *field
sections* (RFC 9204). `Http3HeaderCodec` decodes inbound request field
sections and encodes outbound response field sections; the QPACK
primitives live under `Internal/Http3/QPack`:

- `QPackStaticTable` — the 99-entry static table (RFC 9204 Appendix A),
  with forward (index → field) and reverse (name → index, name+value →
  index) lookups.
- `QPackPrefixedInteger` — the N-bit prefixed integer (RFC 9204 §4.1.1),
  shared by every representation.
- `QPackStringCodec` — string literals (RFC 9204 §4.1.2), Huffman flag +
  prefixed length + octets. Huffman decoding reuses the HPACK
  `HPackHuffmanDecoder` because QPACK and HPACK share the RFC 7541
  Appendix B Huffman code.
- `QPackFieldSectionDecoder` / `QPackFieldSectionEncoder` — the field
  section prefix plus the per-line representations.

### The dynamic table is disabled by default — and why that is RFC-compliant

By default the transport runs with the **QPACK dynamic table disabled**:
the server's `QPACK_MAX_TABLE_CAPACITY` is `0`. RFC 9204 §3.2.3 / §5
explicitly permit this — a decoder that advertises capacity `0` simply
forbids the encoder from ever inserting dynamic entries. It is the
standards-blessed "static-only" QPACK profile, not a partial
implementation, and it stays the default because it collapses several
otherwise-hard problems:

- **No blocked streams.** A stream blocks only when a field section
  references dynamic entries not yet received (RFC 9204 §2.1.2). With the
  table disabled, the Required Insert Count is always 0, so a field
  section can never reference a not-yet-inserted entry — there is nothing
  to block on. `QPACK_BLOCKED_STREAMS` is effectively 0. The decoder
  enforces this by **rejecting any field section whose Required Insert
  Count is non-zero** as a decompression failure (RFC 9204 §2.2).
- **No encoder/decoder instruction processing.** The QPACK encoder and
  decoder unidirectional streams carry only dynamic-table instructions, so
  with the table disabled they carry nothing the server must act on.

### Dynamic table (opt-in)

Opting in is a per-listener, public configuration choice:
`options.UseHttp3(listener, o => o.QPack.MaxTableCapacity = 4096)`. The public
`Http3ConnectionListenerOptions.QPack` (an `Http3QPackOptions`) carries the advertised
capacity and blocked-stream limit; the HTTP/3 registration captures it in an
`Http3ConnectionFactory` and threads it to the connection context. Setting
`MaxTableCapacity` above 0 opts in to the full dynamic table on the **decoder**
side (inbound request field sections). The switch is entirely gated on that
option: when it is 0 (the default) none of the machinery below is constructed
and the static-only path above is taken verbatim. When it is enabled:

- **Dynamic table** (`QPackDynamicTable`). A capacity-bounded, absolutely
  indexed entry store (RFC 9204 §3.2) fed by the peer's encoder-stream
  instructions: Set Dynamic Table Capacity (§4.3.1), Insert with Name
  Reference — static or dynamic (§4.3.2), Insert with Literal Name (§4.3.3),
  and Duplicate (§4.3.4). Entry size is `name + value + 32` octets; inserts
  evict from the draining end to fit, and an insert that cannot fit even an
  empty table, or a capacity above the advertised maximum, is
  `QPACK_ENCODER_STREAM_ERROR`.
- **Instruction codecs.** `QPackEncoderInstructionParser` applies inbound
  encoder instructions incrementally (partial instructions are left buffered
  for the next read); `QPackDecoderInstructionEncoder` emits the decoder
  instructions — Section Acknowledgment (§4.4.1), Stream Cancellation
  (§4.4.2), and Insert Count Increment (§4.4.3).
- **Field-section resolution.** `QPackFieldSectionPrefix` reconstructs the
  Required Insert Count (§4.5.1.1) and Base (§4.5.1.2), and
  `QPackFieldSectionDecoder` resolves dynamic indexed, dynamic name-reference,
  and post-base references (§4.5.2–§4.5.5) against the table. The prefix is
  parsed once (before any blocking) so the Base does not drift as more
  insertions arrive during the wait.
- **Blocked-stream bookkeeping** (`QPackDecoderState`). A field section whose
  Required Insert Count exceeds the current insert count blocks until the
  encoder stream delivers the referenced insertions. The number of
  concurrently blocked streams is capped at the advertised
  `QPACK_BLOCKED_STREAMS`; exceeding it, or an otherwise-unsatisfiable
  Required Insert Count, is a `QPACK_DECOMPRESSION_FAILED` connection error
  (§2.2). Because dynamic-table state is shared across streams, these failures
  terminate the connection rather than dropping a single stream — unlike the
  per-stream failures the static-only path isolates.

The **response encoder stays static-only by design** (see "Encoder" below):
an encoder is never required to use the dynamic table, so responses reference
the static table or literals and never insert. This keeps response encoding
stateless and sidesteps having to track the client decoder's acknowledgments.

> **Known limitation — Section Acknowledgment / Stream Cancellation.** These
> two decoder instructions are keyed on the QUIC **request stream ID**, which
> the `Assimalign.Cohesion.Connections` `IConnection` abstraction does not yet
> surface (the QUIC stream connection reports a synthetic `ConnectionId`, not
> the wire stream ID). The instruction encoders are implemented and unit
> tested, and Insert Count Increment — which needs no stream ID — is emitted
> live, keeping the peer's Known Received Count advancing. Wiring Section
> Acknowledgment / Stream Cancellation into the live path is deferred pending a
> transport surface for the QUIC stream ID (filed as a follow-up).

### Decoder representations

`QPackFieldSectionDecoder` reads the Field Section Prefix (§4.5.1),
requires Required Insert Count = 0, then walks the field lines:

| First-byte pattern | Representation | Handling |
|---|---|---|
| `1Txxxxxx` | Indexed Field Line (§4.5.2) | `T=1` → resolve static index; `T=0` (dynamic) → reject |
| `01NTxxxx` | Literal w/ Name Reference (§4.5.4) | `T=1` → static name + literal value; `T=0` → reject |
| `001NHxxx` | Literal w/ Literal Name (§4.5.6) | literal name + literal value |
| `0001xxxx` | Indexed w/ Post-Base (§4.5.3) | dynamic → reject |
| `0000Nxxx` | Literal w/ Post-Base Name Ref (§4.5.5) | dynamic → reject |

The never-indexed (`N`) bit is accepted and ignored — with no dynamic
table there is no indexing decision to make. Every rejection throws a
parse failure the receive loop isolates per-stream (the offending request
stream is dropped; the connection survives).

### Field-section rules (RFC 9114 §4.2 / §4.3)

After QPACK decoding, `Http3HeaderCodec` enforces the HTTP/3 message
rules:

- **Pseudo-header set.** Only `:method`, `:scheme`, `:authority`,
  `:path`, and `:protocol` (RFC 9220, recognized here, acted on by #339)
  are valid request pseudo-headers; any other is malformed.
- **Ordering.** All pseudo-headers MUST precede regular fields.
- **Uniqueness.** A pseudo-header MUST NOT repeat.
- **Required fields.** A non-CONNECT request MUST carry `:method`,
  `:scheme`, and a non-empty `:path`.
- **Lowercase names.** A regular field name with an uppercase character is
  malformed.
- **Connection-specific fields** are rejected, and `:authority`
  supersedes `Host`, both via the shared `HttpFieldNormalization` (see
  #336) so HTTP/2 and HTTP/3 stay byte-for-byte consistent.

### Encoder

`QPackFieldSectionEncoder` emits a zero Field Section Prefix and prefers,
per field: an Indexed Field Line for an exact static name+value match
(e.g. `:status: 200`), then a Literal with static Name Reference for a
known name, then a Literal with Literal Name. Field names are lowercased
on the wire. The encoder is **static-only**: it never inserts into or
references the dynamic table, so response encoding stays stateless and
needs no acknowledgment tracking (RFC 9204 §2.1.1).

Literal names and values are **Huffman-coded** (RFC 9204 §4.1.2, RFC 7541
Appendix B) when the Huffman form is strictly shorter than the raw octets,
via the shared `HPackHuffmanEncoder`. The encoder lives in the HPACK folder
because HTTP/2 HPACK and HTTP/3 QPACK share the same Appendix B code, so the
HTTP/2 response encoder (`HPackEncoder`) gets the same shorter-of-the-two
literal encoding. `HPackHuffmanEncoder.GetEncodedLength` measures the Huffman
size without allocating, so the choice costs a single pass over the octets.

### AOT posture

No reflection, no runtime code generation, no dynamic dispatch. The
static table is a constant array with dictionaries built at type init;
encoding and decoding are span/stream arithmetic over the prefixed-integer
and string primitives.

### Non-goals

- **Response-side dynamic table.** The response encoder is static-only by
  design (above): it never inserts into a dynamic table of its own, so the
  server never opens a QPACK *encoder* stream and never has to track the
  client decoder's Section Acknowledgments to encode safely. The dynamic
  table implemented here is decoder-side only (inbound requests).
- **Live Section Acknowledgment / Stream Cancellation.** Implemented and unit
  tested as instruction encoders, but not emitted on the live path pending a
  transport surface for the QUIC request stream ID (see the Known limitation
  above). Insert Count Increment is emitted live.
- **Acting on the peer's `QPACK_MAX_TABLE_CAPACITY`.** The server reads the
  peer's SETTINGS but, being a static-only encoder, does not use the peer's
  advertised decoder capacity to size a response-side table.

## Extended CONNECT (`:protocol`)

### What it is

Extended CONNECT (RFC 8441 for HTTP/2, RFC 9220 for HTTP/3) lets a client
bootstrap another protocol — most commonly WebSocket — over a single
HTTP/2 or HTTP/3 stream by sending a `CONNECT` request that additionally
carries the `:protocol` pseudo-header. Cohesion **recognizes and models**
extended CONNECT explicitly so an application can detect it and respond
deterministically.

### The model: an explicit feature, not a baseline side effect

A valid extended CONNECT installs an `IHttpExtendedConnectFeature` on the
exchange's feature collection, exposing the requested `:protocol`. Ordinary
requests carry no such feature, so `context.IsExtendedConnect` is `false`
and `context.ExtendedConnect` is `null` for them. Modeling the transition
as an opt-in feature — rather than, say, a flag baked into every request —
keeps it an explicit extension surface (per the issue's framing) and means
baseline request handling is unchanged for the common case.

The feature contract (`IHttpExtendedConnectFeature`) and the
`context.IsExtendedConnect` / `context.ExtendedConnect` ergonomics live in
the core `Assimalign.Cohesion.Http` library; the transport produces the
internal implementation. Recognition (`:protocol`), validation, and the
`IsExtendedConnect` / `ValidateExtendedConnect` rules are shared between
HTTP/2 and HTTP/3 via `HttpFieldNormalization` so both versions behave
identically.

### Deterministic validation (RFC 8441 §4 / RFC 9220)

- `:protocol` on a **non-CONNECT** request is malformed.
- An extended CONNECT (CONNECT + `:protocol`) MUST also carry `:scheme`,
  `:path`, and `:authority`; a missing one is malformed.
- `:protocol` MUST NOT appear more than once.

A violation fails deterministically — never a silent downgrade. HTTP/2
surfaces it as the same field-section failure the receive loop maps to a
connection `PROTOCOL_ERROR` (GOAWAY); HTTP/3 drops the offending stream
(the connection survives).

### Advertising `SETTINGS_ENABLE_CONNECT_PROTOCOL`

- **HTTP/2** advertises `SETTINGS_ENABLE_CONNECT_PROTOCOL = 1` (id `0x8`)
  in its initial SETTINGS (RFC 8441 §3), telling peers they may use
  extended CONNECT.
- **HTTP/3** advertises the same. The server opens its own *unidirectional*
  control stream via
  `IMultiplexedConnection.OpenStreamAsync(ConnectionDirection.WriteOnly)`
  and sends a SETTINGS frame carrying `SETTINGS_ENABLE_CONNECT_PROTOCOL`
  (0x08) = 1 as its first frame — see "The server control stream and
  SETTINGS emission" above. This matches the HTTP/2 posture, so a client
  may initiate extended CONNECT (`CONNECT` + `:protocol`) over HTTP/3, and
  the request is then recognized, validated, and modeled identically to
  HTTP/2 — there is no silent downgrade in either direction.

### No tunnel — scope boundary

The feature exposes the requested protocol; it does **not** surrender a
tunnel stream or implement WebSocket framing. Cohesion does not retain a
WebSocket transport/API surface, so per the issue's "implement only if the
transport and API surface are intentionally retained" guidance, the actual
WebSocket bootstrap (the post-2xx data tunnel) is out of scope. An
application that wants to act on an extended CONNECT reads the feature and
drives its own response; the framework neither fabricates a tunnel nor
pretends one exists.

### AOT posture

No reflection or runtime codegen. Recognition is pseudo-header dispatch;
validation is string comparison; the feature is a two-property record-like
class resolved through the existing feature collection.

### Non-goals

- **WebSocket framing / the data tunnel.** See above.
- **Classic CONNECT tunneling.** A `CONNECT` without `:protocol` is surfaced
  as an ordinary CONNECT request; opaque TCP tunneling is not implemented.

## RFC 9218 extensible priorities

### What it is

The server implements the RFC 9218 Extensible Prioritization Scheme and
**replaces** — does not extend — the deprecated RFC 7540 tree-priority handling
(RFC 9113 §5.3.2 permits ignoring it). Three moving parts:

- **Signals in, priority out.** The `Priority` request header and the
  `PRIORITY_UPDATE` frame both carry the same `u`/`i` Priority Field Value. Both
  are parsed through the core-Http structured-field toolkit into an
  `HttpPriority` (urgency 0–7, incremental flag) — no field-value parsing is
  reimplemented in the transport (Lane B owns parsing, Lane A owns scheduling).
  The shared `HttpPriorityFieldValue` helper bridges the ASCII frame octets to
  the `char` span the toolkit consumes.
- **Per-stream effective priority.** Each stream carries an effective priority:
  the `Priority` header initialises it, and a `PRIORITY_UPDATE` overrides it and
  pins it so a later header parse cannot clobber it (RFC 9218 §8), regardless of
  arrival order.
- **Urgency-ordered write scheduling** on the contended HTTP/2 write path.

### Replacing the legacy PRIORITY frame

The RFC 7540 `PRIORITY` frame (type `0x2`) and its stream-dependency/weight/
exclusive model are gone from the engine: the frame model, its reader fields, and
its writer case were deleted, and `0x2` is now simply ignored (RFC 9113 §5.3.2).
The HEADERS frame's optional priority fields are still *skipped* during parsing —
they must be, to locate the header block — but they are no longer read as a
scheduling signal. HTTP/2 advertises `SETTINGS_NO_RFC7540_PRIORITIES = 1`
(RFC 9218 §2.1) in its initial SETTINGS so peers know to use the header +
`PRIORITY_UPDATE` scheme; an inbound value other than 0/1 is a `PROTOCOL_ERROR`.

### HTTP/2 PRIORITY_UPDATE (frame type 0x10)

Dispatched like any other connection-control frame. A `PRIORITY_UPDATE` on a
non-zero stream is a connection error (`PROTOCOL_ERROR`, RFC 9218 §7.1), as is a
Prioritized Stream ID that is zero or even (a client cannot open those). The
4-octet Prioritized Stream ID is parsed as a fixed frame-prefix field (the same
mechanism GOAWAY uses), leaving the ASCII Priority Field Value as the frame
payload. A field value that cannot be parsed leaves the frame **without effect**
(not an error). A frame referencing a stream that has not opened yet is retained
in a small bounded buffer and applied when the stream is created; a frame
referencing an already-closed stream is dropped.

### The write scheduler

`Http2WriteScheduler` replaces the plain FIFO write semaphore. It preserves the
non-interleaving invariant (RFC 9113 §4.1 — exactly one writer holds the gate at a
time) and, when writers contend, grants the gate in RFC 9218 §10 order instead of
first-come-first-served:

1. connection-control frames first (a sentinel urgency below 0, so ACKs / window
   updates / GOAWAY are never starved behind response data);
2. then response writes by ascending urgency;
3. non-incremental before incremental at the same urgency;
4. round-robin by stream id among same-urgency incremental streams.

The ordering policy is a pure, synchronous function (`SelectNextWaiterIndex`) so
it is unit-tested in isolation, separate from the async gate. Both response write
paths go through it:

- The **buffered** path (`SendAsync`) holds the gate for the whole contiguous
  HEADERS [+ CONTINUATION…] [+ DATA…] sequence, so the scheduler orders **which
  stream's queued response proceeds next** under contention.
- The **streaming** path acquires the gate **per DATA frame** — and only after
  the send-window credit for that frame has been granted, so a writer parked on
  flow control never holds the gate. This is what delivers real frame-level
  interleaving: same-urgency incremental streams round-robin DATA frame by DATA
  frame, and a newly-arrived lower-urgency (more urgent) response preempts
  between frames of a less urgent one.

### HTTP/3 posture

HTTP/3 multiplexes streams at the QUIC layer, so there is no shared connection
write gate to schedule — response ordering across streams is delegated to the QUIC
transport. What the HTTP/3 engine owns is the **priority signal as observable
state**:

- The peer's control-stream drain now parses post-SETTINGS `PRIORITY_UPDATE`
  frames instead of discarding them: a request-stream update (`0xF0700`) records
  the referenced stream's effective priority in the connection's priority map;
  the request `Priority` header sets the per-request effective priority on the
  context. Both are observable to the engine.
- A push `PRIORITY_UPDATE` (`0xF0701`) references a push id that cannot exist (the
  server issues no pushes — see the server-push scope decision below). It is
  rejected and draining stops. Consistent with this drain's existing
  parse-and-discard posture, the rejection is recorded and connection teardown
  closes the QUIC connection; strict HTTP/3 would signal `H3_ID_ERROR`.

### AOT posture

No reflection or dynamic dispatch. Frame parsing is span-based; the priority
field-value bridge stack-allocates for the small values that occur in practice and
falls back to a pooled buffer otherwise. The scheduler is a plain lock + list with
a pure selection function. Builds clean under the trim/AOT analyzers
(`IsAotCompatible=true`).

## Scope decision: server push (de-scoped)

Cohesion **does not implement HTTP/2 or HTTP/3 server push.** This is a
deliberate, recorded decision, not an implementation gap:

- Server push has effectively failed in the field. Chromium disabled and
  then removed HTTP/2 push (2022), and HTTP/3 push sees negligible
  real-world client support. The complexity (push streams, `PUSH_PROMISE`,
  `MAX_PUSH_ID` / `CANCEL_PUSH` bookkeeping, cache-state assumptions) buys
  almost nothing for interoperability today, and `103 Early Hints` covers
  the practical "warm the client early" use case without it — and `103` now
  ships as an interim response on all three versions (see "Interim (1xx)
  responses and `Expect: 100-continue`" above), so the substitute is real,
  not aspirational.
- The mechanism is optional for a compliant server: RFC 9113 §8.4 and
  RFC 9114 §4.6 permit a server to simply never push.

**Enforcement** (so the decision is real, not just documentation):

- **HTTP/2** advertises `SETTINGS_ENABLE_PUSH = 0` in its initial SETTINGS
  (a server's own ENABLE_PUSH is informational, but we state intent), never
  emits `PUSH_PROMISE`, and **rejects an inbound `PUSH_PROMISE` as a
  connection error of type `PROTOCOL_ERROR`** — which is also exactly what
  RFC 9113 §8.4 requires of a server, since only servers may push and a
  client therefore must never send one. Without the explicit rejection the
  frame would fall through the dispatch and be silently ignored.
- **HTTP/3** never opens a push stream and never sends `PUSH_PROMISE`. The
  HTTP/3 stream engine rejects server-only frames (including `PUSH_PROMISE`)
  arriving on a client-initiated request stream as `H3_FRAME_UNEXPECTED`
  (enforced in the HTTP/3 stream/SETTINGS engine). A client's `MAX_PUSH_ID`
  is harmless and ignored because the server never pushes.

**Reversibility.** If a concrete consumer ever needs push, the frame types
are already defined (`Http2FrameType.PushPromise`, `Http3FrameType.PushPromise`,
`MaxPushId`); re-scoping would add a push-stream send path and flip the
rejection into acceptance behind a configuration opt-in. The decision is
documented here so a future reader does not mistake the absence for an
oversight.

## Open questions / future work

- A full design write-up covering the protocol context hierarchy
  (`HttpConnection` / `HttpConnectionContext` / per-protocol
  implementations) is still owed. See `DESIGN_SUGGESTION.md` for the
  in-flight multiplex-aware refactor proposal.
- Async feature initialization (see "Non-goals" above) is worth
  revisiting once a concrete consumer appears that genuinely needs it.

## Scope decision: HTTP datagrams and capsules (de-scoped)

Cohesion **does not implement HTTP datagrams or the capsule protocol**
(RFC 9297). This is a deliberate, recorded decision:

- The capsule protocol and HTTP datagrams exist to carry non-HTTP payloads
  inside an HTTP exchange — primarily for `CONNECT-UDP` (MASQUE,
  RFC 9298), `CONNECT-IP` (RFC 9484), and WebTransport. All of those are
  built **on top of** extended `CONNECT` (RFC 9220 / `:protocol`), so
  datagrams are only meaningful once an extended-CONNECT consumer exists.
- There is no concrete consumer in the Cohesion tree today. The issue's
  own framing makes this explicitly later-wave, optional, extension-shaped
  work ("only after the core HTTP/3 stack is stable and the project has a
  concrete use case"). Shipping a datagram/capsule surface now would be
  speculative API with no caller to validate it.
- HTTP datagrams over HTTP/3 also depend on the QUIC `DATAGRAM` extension
  (RFC 9221) being negotiated at the transport, which the QUIC transport
  does not currently expose.

**Extension seam for the future.** When a concrete consumer arrives (most
likely WebTransport or a MASQUE proxy), the capsule protocol is a framing
layer over the request/response body stream and the HTTP-datagram flow
rides the extended-CONNECT stream established by #339. Neither requires
changes to the baseline request/response model — they attach as an
opt-in capability on an already-upgraded exchange — so de-scoping now does
not paint the design into a corner. The decision is recorded here so the
absence is understood as intentional rather than an oversight.
