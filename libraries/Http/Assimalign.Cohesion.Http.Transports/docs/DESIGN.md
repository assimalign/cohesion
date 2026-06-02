# Assimalign.Cohesion.Http.Transports — Design

This document captures the design intent behind the shipped HTTP transport
surface. It is intentionally focused on the design decisions a future
reader (or future Claude session) would otherwise have to re-derive from
diffs. `DESIGN_SUGGESTION.md` in this same folder is a separate,
forward-looking proposal for a multiplex-aware refactor; this file
describes the surface as it ships today.

## Per-request feature injection

### What it is

`HttpConnectionListenerOptions.CreateFeatures` is the single injection
hook the transport layer exposes for application code to pre-populate the
feature collection on every `IHttpContext`:

```csharp
HttpConnectionListenerOptions options = new()
{
    CreateFeatures = () =>
    {
        HttpFeatureCollection features = new();
        features.Set(new SessionFeature(/* scoped per-request state */));
        features.Set(new TracingFeature(/* request-scoped tracer */));
        return features;
    }
};
```

The factory is invoked once per `IHttpContext` being constructed — once
per HTTP/1.1 request, once per HTTP/2 / HTTP/3 stream. The returned
collection becomes the defaults source of the per-request
`IHttpContext.Features` so the features it carries are immediately
visible to the first middleware to read them.

### Why per-request, not per-connection

A previous iteration of this surface bound features to the *connection*
lifetime via a `ConfigureConnectionFeatures` callback that fired once per
accepted connection. That shape was wrong:

- `IHttpContext` is `IAsyncDisposable` and tears down at the end of every
  request. Features whose state needs deterministic cleanup
  (cryptographic material, scoped service providers, file handles, span
  buffers) only get that cleanup if the framework can dispose them
  per-request. A connection-scoped collection has no equivalent dispose
  hook — connection teardown happens on a different timeline (keep-alive
  pooling, HTTP/2 GOAWAY, HTTP/3 idle timeout) and would force every
  feature author to invent their own cleanup signalling.
- HTTP/2 and HTTP/3 multiplex many requests over one connection. A
  connection-scoped feature with mutable state quickly becomes a shared
  data race waiting to happen. Per-request scoping eliminates that
  category of bug by construction.
- The connection lifetime is invisible to most consumers. Application
  code reasons in terms of requests; binding feature lifetime to the
  request matches what middleware actually expects.

The `CreateFeatures` factory therefore fires per-request, the returned
collection lives only as long as the `IHttpContext` does, and disposal
is the framework's contract — not the feature author's.

### Disposal contract

When `IHttpContext.DisposeAsync` runs, the transport walks the effective
feature collection (the local layer plus the factory-supplied defaults)
and disposes every feature that implements `IAsyncDisposable` or
`IDisposable`:

- `IAsyncDisposable.DisposeAsync` is preferred over `IDisposable.Dispose`
  when the feature implements both.
- A single feature throwing during disposal does **not** abort the
  disposal walk. The exception is swallowed and the remaining features
  (and the request / response body streams) are disposed normally. This
  prevents one faulty feature from leaking the rest of the request's
  resources.
- Replaced features (a `Set` call that overwrites a same-named feature)
  are no longer reachable through enumeration and therefore are not
  disposed by the framework. Middleware that replaces a feature is
  responsible for disposing the old instance explicitly.
- Snapshot semantics: the feature list is enumerated once into an array
  before disposal begins so a feature whose `DisposeAsync` mutates the
  collection cannot break iteration.

### Why a factory rather than a callback

`CreateFeatures` returns a new `IHttpFeatureCollection` rather than
populating one the framework hands in. The factory shape is the
right primitive for two reasons:

1. **Disposal scope is unambiguous.** The framework owns the returned
   collection's lifetime: it wraps it as defaults for the request
   collection and disposes its features when the context disposes. With
   a populate-callback the caller would need to coordinate disposal of
   the framework-owned collection, which inverts the ownership model.
2. **Per-request state stays in the closure.** Many useful features
   capture per-request constructor arguments (a fresh tracer, a fresh
   scoped service provider). A factory naturally allocates fresh
   per-request state on each call; a populate callback would force the
   caller to manage per-request allocation themselves.

### Wrapping semantics

`HttpContext.Features` is typed as the concrete `HttpFeatureCollection`,
but `CreateFeatures` returns the `IHttpFeatureCollection` interface so
the user can supply any implementation. The transport reconciles this by
wrapping the factory result with
`new HttpFeatureCollection(factoryResult)`, which uses the factory
result as a read-through defaults source:

- Reads (`Get`, enumeration) pass through to the factory's collection
  when no local override exists.
- Mutations (`Set`, `Remove`) on `IHttpContext.Features` land on the
  local layer only — the factory's collection is not modified by
  middleware. This is the safer default: factory-attached features
  cannot be silently replaced or removed by downstream middleware.
- Disposal walks the effective collection (local + defaults) so
  features attached by either layer get cleaned up uniformly.

### Synchronous, no transport-context input

The factory is `Func<IHttpFeatureCollection>?` — no parameters, no
async. This is deliberate:

- Async would mean the receive loop has to await on the request-build
  path, which would interleave with the protocol parser and complicate
  reasoning about flow-control and back-pressure. The factory's job is
  to allocate per-request state, not to do I/O.
- Connection metadata (endpoints, IsSecure) is not passed in because the
  factory's caller typically does not need it; what they need
  (per-request service provider, per-request diagnostics scope) is
  captured in the closure. Pushing transport state into the factory
  signature would invite features that depend on connection lifetime,
  which is exactly the lifetime mismatch we are walking away from.

### AOT posture

No reflection, no runtime code generation. The factory is a plain
delegate invocation and the feature lookup chains through
`HttpFeatureCollection`'s dictionary reads.

### Non-goals

- **Connection-scoped state.** If a feature genuinely needs to live for
  the lifetime of the connection (a TLS handshake projection, for
  example), it belongs in the application layer's own connection map
  keyed on `IHttpConnectionContext.Items` or a sidecar dictionary —
  *not* on the request's feature collection. This package does not
  surface a connection-feature collection at all.
- **Multiple registered factories.** A pipeline of feature contributors
  can be composed by the caller — the single `CreateFeatures` slot can
  invoke any number of internal helpers.
- **Async initialization.** See above. If a concrete use case for
  per-request async feature setup emerges, the factory signature can be
  widened without breaking existing callers.

## IsSecure propagation from transport middleware

### What it is

`HttpContext.ConnectionInfo.IsSecure` reports whether the underlying
transport carrying this request is currently secured. Two signals
combine to produce that value:

1. **Registration-time hint.** When a transport is registered with
   `HttpConnectionListenerOptions.UseTransport(..., isSecure: true)` (or
   via the convenience `UseHttp3` which always sets it true), that hint
   is captured on `HttpProtocolRegistration.IsSecure`. This is the
   "operator knows up-front" path &#8212; TLS terminated at an upstream
   load balancer, QUIC's always-on encryption, mTLS provisioned outside
   the process.

2. **Transport-pipeline signal.** Transport-level middleware that
   establishes a secure session at runtime (e.g. `SslStream` wrapping
   a raw TCP socket) records the fact via the typed
   `ITransportConnectionContext.IsSecure` extension shipped in
   `Assimalign.Cohesion.Transports`. The middleware writes
   `context.IsSecure = true;` after a successful handshake.

The HTTP layer's effective value is `registrationHint || transportReports`.
The OR is deliberate: a registration that is explicitly secure stays
secure even when the transport never sets the flag (no down-negotiation),
and a transport that newly reports secure promotes the connection even
when the registration was left as the default `false`.

### Where the probe runs

The probe lives in each protocol's `*Connection.OpenAsync`
(`Http1Connection`, `Http2Connection`), at the single point where the
HTTP connection context is constructed:

```csharp
ITransportConnectionContext transportContext = await _connection.OpenAsync(token);
bool effectiveIsSecure = IsSecure || transportContext.IsSecure;
_openContext = new Http1ConnectionContext(transportContext, effectiveIsSecure, CreateFeatures);
```

This timing is correct because the transport pipeline (where TLS
middleware runs) executes *inside* `_connection.OpenAsync`. By the time
the `await` returns, every middleware on the connection has run and
the transport context's `Items` are fully populated. There is no race
between the probe and the handshake.

The effective value is then baked into `HttpConnectionInfo.IsSecure`
and carried into every per-request `HttpContext.ConnectionInfo` on the
connection &#8212; the probe runs once per connection, not once per
request.

### Why the extension lives in Assimalign.Cohesion.Transports

The `IsSecure` extension is a transport-layer concern, not an
HTTP-layer concern: any future consumer of a transport (HTTP, RPC,
WebSocket, custom protocol) needs to know whether the connection
beneath them is secured, and any transport-level middleware (TLS,
mTLS, peer-authentication proxies) needs a typed way to record that
without depending on a higher protocol library. Placing the extension
in the abstractions library keeps the dependency direction correct.

Storage is the existing `ITransportConnectionContext.Items` dictionary
under the `TransportSecurityExtensions.IsSecureItemKey` constant; the
extension property handles the cast and missing-key path so consumers
do not reach into the dictionary directly.

### HTTP/3

`Http3Connection` does not run this probe because HTTP/3 is always
secured (QUIC requires TLS 1.3 by construction). The
`UseHttp3(...)` registration helper pins `isSecure: true` and the
`Http3ConnectionContext` flows that through unchanged.

### Non-goals

- **Mid-connection upgrade (STARTTLS / `Upgrade: TLS/1.0`).** Once the
  connection's effective `IsSecure` is computed at `OpenAsync` time it
  stays for the lifetime of the connection. The transport probe runs
  once; per-request flipping is not supported. RFC 2817 in-band TLS
  upgrade over HTTP/1.1 would require a separate, explicit
  re-construction of the connection context and is intentionally out
  of scope.
- **Down-negotiation.** A registration that declared `isSecure: true`
  cannot be demoted by a transport that fails to set the flag. The OR
  rule guarantees this in both directions.
- **Rich TLS metadata** (client certificate, ALPN, cipher suite).
  Future work; the same `Items`-backed pattern accommodates additional
  typed extensions next to `IsSecure` without changing this design.

## Receive-loop failure isolation

Each protocol's receive loop (`Http1ConnectionContext.ReceiveAsync`,
`Http2ConnectionContext.ReceiveAsync`, `Http3ConnectionContext.ReceiveAsync`)
classifies failures into two scopes:

- **Per-connection wire-level failures** — truncated frames, malformed
  request lines, peer reset, socket I/O errors. The receive enumerable
  yields no more values and exits cleanly; the surrounding
  `await using` disposes the connection; the listener keeps accepting.
  Protocol-required wire frames (`GOAWAY` on HTTP/2 connection errors,
  `RST_STREAM` on HTTP/2 stream errors) are emitted before exit.
- **Per-stream failures** (HTTP/2, HTTP/3) — malformed headers on one
  stream, QPACK errors on one HTTP/3 stream. The loop emits `RST_STREAM`
  (HTTP/2) or drops the offending stream (HTTP/3) and continues
  accepting subsequent streams on the same connection.

The design intent is *failure isolation*: a single malformed peer must
never bring down the listener. Cancellation propagates normally so
cooperative shutdown is unaffected.

## Scope decision: server push (de-scoped)

Cohesion **does not implement HTTP/2 or HTTP/3 server push.** This is a
deliberate, recorded decision, not an implementation gap:

- Server push has effectively failed in the field. Chromium disabled and
  then removed HTTP/2 push (2022), and HTTP/3 push sees negligible
  real-world client support. The complexity (push streams, `PUSH_PROMISE`,
  `MAX_PUSH_ID` / `CANCEL_PUSH` bookkeeping, cache-state assumptions) buys
  almost nothing for interoperability today, and `103 Early Hints` covers
  the practical "warm the client early" use case without it.
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
