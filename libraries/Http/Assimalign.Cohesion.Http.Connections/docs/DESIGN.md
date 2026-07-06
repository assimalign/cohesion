# Assimalign.Cohesion.Http.Transports — Design

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
    options.UseHttp2(tlsTcpListener);              // IConnectionListener (TLS pre-composed)
    options.UseHttp3(quicListener);                // IMultiplexedConnectionListener
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
  GOAWAY drain, HTTP/1.1 response drain) before disposal.
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

`HttpConnectionListenerOptions.Interceptors` is the injection seam the transport
exposes for code outside this package to participate at request-parse time. The
contract — `IHttpRequestInterceptor`, `HttpRequestInterceptorContext`, and the
typed rejection `HttpRequestRejectedException` — lives in core
`Assimalign.Cohesion.Http` (a generic seam, like `IHttpFeature`), so feature
packages implement hooks without referencing this transport package and this
transport never references them:

```csharp
HttpConnectionListenerOptions options = new();
options.Interceptors.Add(HttpRequestLimits.CreateMaxRequestBodySizeInterceptor());
options.Interceptors.Add(new RequestDigestInterceptor(/* parse-time hashing */));
```

Per HTTP/1.1 request the parser:

1. Parses the head (request line + headers) under the configured limits, then
   derives host/scheme.
2. Builds one `HttpRequestInterceptorContext` — head data, a **read-only**
   header view (`HttpHeaderCollection.AsReadOnly()`), a fresh feature
   collection, and the body-size knob seeded from
   `HttpServerLimits.MaxRequestBodySize` — and runs every head hook in
   registration order.
3. Freezes the knob and reads the body under whatever cap the hooks left
   (413 on violation, as ever).
4. Materializes the body stream and runs every body hook in registration
   order, each receiving the previous result — the last registered interceptor
   produces the outermost wrapper. CONNECT tunnels skip body hooks; empty
   bodies still run them.
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
> supersedes it: `OnRequestHead` + `Features.Set` covers feature seeding and
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

- **`HttpRequestRejectedException`** (4xx/5xx-constrained) is caught explicitly
  in `Http1ConnectionContext.TryReadRequestAsync` — ahead of the wire-level
  classifier — answered with a minimal bodyless status response, and the
  connection is closed (never reused: remaining wire state is indeterminate).
  This is the sanctioned way for a hook to refuse a request.
- **`IOException`-family exceptions** thrown by a hook are indistinguishable
  from wire failures and get silently classified as such (connection dropped,
  no response). This is a documented hazard, not a feature: hooks must use the
  typed rejection for control flow.
- **Anything else** propagates — programmer errors are not masked, matching the
  receive-loop failure-isolation philosophy.

Hooks run inline on the parse path at a point where the request-headers
deadline has been disarmed, so they must be CPU-only; a blocking hook stalls
the connection and pins a thread-pool thread.

### Disposal contract

When `IHttpContext.DisposeAsync` runs, the transport walks the effective
feature collection and disposes every feature implementing `IAsyncDisposable`
or `IDisposable` (async preferred; one throwing feature does not abort the
walk; the list is snapshotted before disposal so a mutating `DisposeAsync`
cannot break iteration). Features attached by head hooks and by middleware are
treated identically. A body-stream wrapper owns the stream it wraps: disposing
the outermost stream (which the exchange's disposal triggers via
`Request.Body.Dispose()`) must dispose the whole chain.

The contract also covers requests that never become an exchange. If the parse
fails **after** head hooks ran — a limit rejection (413/431), a hook rejection,
a malformed body, a wire failure, or a timeout — no `IHttpContext` exists to
own the disposal walk, so the parser itself tears down the partially-built
wrapper chain and disposes every hook-attached feature (same walk semantics)
before the failure surfaces. Hook-attached disposables therefore never leak on
the rejection paths an attacker can drive for free (e.g. an oversized
`Content-Length` declaration, rejected before any body byte is read).

### Feature-collection plumbing

The parser hands its `HttpFeatureCollection` to the context constructor, which
uses it **directly** — no defaults-wrapper layer, which would add a second
dictionary probe to every `Get` on the hot path. A `null` collection (the
fast path) gets a fresh empty one; a foreign `IHttpFeatureCollection`
implementation is wrapped as a read-through defaults source for safety.

### Protocol coverage

The hooks are wired into the HTTP/1.1 parser. HTTP/2 and HTTP/3 currently
buffer a stream's body before decoding its head (`Http2Stream.CreateContext`
runs only once the stream is complete; the h3 request stream is drained before
header decode), so the same two hook points exist conceptually but the
"cap adjustable before enforcement" property is not implementable there until
those paths enforce body caps at all — tracked as follow-up work alongside the
h2/h3 abuse-limit items. Until then the seam is h1-only at runtime, which
matches the pre-seam behavior (the transport-seeded body-size feature was also
h1-only).

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

`HttpConnectionListenerOptions.Limits` (`HttpServerLimits`) is the tuning
surface, with conservative Kestrel-`KestrelServerLimits`-parity defaults so a
listener is protected out of the box:

| Limit | Default | Enforced by | Rejection |
|---|---|---|---|
| `MaxRequestLineSize` | 8 KB | `Http1MessageReader` request-line read | `414` URI Too Long (RFC 9110 §15.5.15) |
| `MaxRequestHeaderCount` | 100 | header loop | `431` Request Header Fields Too Large (§15.5.22) |
| `MaxRequestHeadersTotalSize` | 32 KB | per-line cap = remaining budget | `431` |
| `MaxRequestBodySize` | ~28.6 MB (`null` = unbounded) | `Http1MessageBodyReader` | `413` Content Too Large (§15.5.14) |
| `KeepAliveTimeout` | 130 s | `Http1ConnectionContext` | connection reclaimed |
| `RequestHeadersTimeout` | 30 s | `Http1ConnectionContext` | `408` Request Timeout (§15.5.9) |

The limits flow listener → `Http1Connection` → `Http1ConnectionContext` →
reader as a plain object reference; there is no DI, config, or logging
dependency in this package (Lane A guardrail — config binding of these limits is
a Web.Hosting builder-time concern).

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
   body read is bounded only by the ambient connection token. Body-read data-rate
   limits (`MinRequestBodyDataRate`) are deliberately deferred behind the
   streaming-body rework — the reader fully buffers bodies today, so a data-rate
   gate is meaningless until the read is incremental.

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
injection" above): the parser seeds `HttpRequestInterceptorContext
.MaxRequestBodySize` from `Limits.MaxRequestBodySize`, head hooks may adjust
it, the parser freezes it and enforces whatever value remains (413 on
violation). The typed `IHttpMaxRequestBodySizeFeature` lives in the
`Assimalign.Cohesion.Http.RequestLimits` package, whose interceptor attaches a
write-through view over the context knob; this transport knows nothing about
it. Today the read is fully buffered before dispatch, so the writable window is
the head-hook phase; the streaming-body rework moves the freeze to the first
body byte and opens the window to middleware without changing this contract.

### Scope boundary

These limits cover the HTTP/1.1 read path only. HTTP/2 abuse limits
(rapid-reset, CONTINUATION flood, header-list size) and HTTP/2 body-buffering
backpressure are governed by the frame/flow-control machinery and tracked
separately; `MaxConcurrentConnections` is an accept-loop concern owned by the
Web-runtime rewrite, not this surface.

### AOT posture

No reflection, no codegen. The limits are plain properties with guard-clause
validation; enforcement is byte counting and `CancellationTokenSource.CancelAfter`
timer arithmetic.

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
- **`QPACK_MAX_TABLE_CAPACITY` (0x01) = 0** (RFC 9204 §5) states explicitly
  that the QPACK dynamic table is disabled (see the QPACK section), rather
  than relying on the peer to assume the default.

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
request. Post-SETTINGS frames are read but inert in this subset — `GOAWAY`
(§7.2.6) is discarded (graceful-drain handling is deferred to the GOAWAY
work item), and `MAX_PUSH_ID` (§7.2.7) is discarded because the server never
pushes. The drain exists so those frames cannot accumulate unread in the
pipe; it stops on end-of-stream, connection teardown, or a per-stream parse
failure and never throws into the receive loop.

### QPACK encoder/decoder streams

Each of the QPACK encoder (0x02) and decoder (0x03) streams may appear
at most once (RFC 9204 §4.2); a duplicate is a connection error. With
the QPACK dynamic table disabled (`QPACK_MAX_TABLE_CAPACITY = 0`, the
posture #335 builds on) these streams carry no instructions the server
must act on, so accepting the stream and recording that it was seen is
sufficient. The streams are not drained frame-by-frame.

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
therefore connection-first: `Http3Connection.DisposeAsync` delegates to the
multiplexed connection, whose dispose completes bidirectional (request)
streams — delivering any in-flight response data — then closes the QUIC
connection (`CONNECTION_CLOSE` with the transport's configured close
code, `H3_NO_ERROR` by default on the QUIC driver's options), and only
then releases the unidirectional streams locally, after the close means no
stream-level frames can reach the peer. The ordering lives in the QUIC
driver (`QuicMultiplexedConnection`), not here: any multiplexed protocol
with long-lived unidirectional control channels needs the same discipline.

The context's own teardown (`ShutdownAsync`, run from the receive loop's
`finally`) is deliberately minimal: it cancels the inbound control-stream
drain and awaits it, but **never completes, aborts, or FINs the outbound
control stream**. Completing it early — before the connection close — is
exactly the `H3_CLOSED_CRITICAL_STREAM` violation the connection-first
ordering exists to avoid, so the context leaves the outbound critical stream
for the multiplexed connection's dispose to release alongside the close. A
`GOAWAY`-announced graceful drain ahead of the close remains future work
(see Non-goals).

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

- **Acting on post-SETTINGS control frames.** `GOAWAY`, `MAX_PUSH_ID`,
  and friends are now *drained* (parsed and discarded by the background
  control-stream drain, so they cannot accumulate unread) but are not
  *acted on* in this subset: the server never pushes, so `MAX_PUSH_ID` is
  inert, and graceful `GOAWAY`-driven drain is future work.
- **Emitting server GOAWAY / MAX_PUSH_ID.** The server control stream
  carries only the opening SETTINGS frame today; sending `GOAWAY` (for
  graceful drain) rides on it as future work.
- **QPACK dynamic table.** Encoder/decoder streams are accepted but not
  processed; the static-table + Huffman + literal field-section support
  (#335) runs with the dynamic table disabled.
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

### The dynamic table is disabled — and why that is RFC-compliant

The supported feature set runs with the **QPACK dynamic table disabled**:
the server's `QPACK_MAX_TABLE_CAPACITY` is `0`. RFC 9204 §3.2.3 / §5
explicitly permit this — a decoder that advertises capacity `0` simply
forbids the encoder from ever inserting dynamic entries. It is the
standards-blessed "static-only" QPACK profile, not a partial
implementation.

Disabling the dynamic table collapses several otherwise-hard problems:

- **No blocked streams.** A stream blocks only when a field section
  references dynamic entries not yet received (RFC 9204 §2.1.2). With the
  table disabled, the Required Insert Count is always 0, so a field
  section can never reference a not-yet-inserted entry — there is nothing
  to block on. `QPACK_BLOCKED_STREAMS` is effectively 0. The decoder
  enforces this by **rejecting any field section whose Required Insert
  Count is non-zero** as a decompression failure (RFC 9204 §2.2).
- **No encoder/decoder instruction processing.** The QPACK encoder and
  decoder unidirectional streams (handled by the #334 stream engine)
  carry only dynamic-table instructions, so with the table disabled they
  carry nothing the server must act on.

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
on the wire. Huffman coding is **not** applied on the encode path — it is
optional for an encoder (RFC 9204 §4.1.2) and raw octets keep the output
deterministic and allocation-light. (The decode path fully supports
Huffman.)

### AOT posture

No reflection, no runtime code generation, no dynamic dispatch. The
static table is a constant array with dictionaries built at type init;
encoding and decoding are span/stream arithmetic over the prefixed-integer
and string primitives.

### Non-goals

- **QPACK dynamic table** (insertions, duplication, eviction, blocked
  streams). Disabled by design as above; re-enabling would add a dynamic
  table, encoder/decoder-stream instruction processing, and blocked-stream
  bookkeeping behind the same `Http3PeerSettings` seam.
- **Encoder Huffman coding.** Decode supports it; encode emits raw octets.

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
