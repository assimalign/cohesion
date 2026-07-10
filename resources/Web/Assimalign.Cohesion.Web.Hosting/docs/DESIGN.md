# Assimalign.Cohesion.Web.Hosting — Design

`Assimalign.Cohesion.Web.Hosting` is the composition root for the Web resource: it
builds a `WebApplication` (a `Host<WebApplicationContext>`), wires the middleware
pipeline, and owns the runtime server that turns accepted HTTP connections into
pipeline invocations. It is the **only** Web layer where DI, logging, and
configuration are integrated, and that integration is strictly builder-time —
nothing resolves services per request.

This document focuses on the piece with the most load-bearing runtime behaviour:
`WebApplicationServer`, the default `IWebApplicationServer`. Its dispatch model
and stop semantics are the contract the rest of the Web middleware stack builds
on, so they are recorded here rather than left to be re-derived from the code.

## Design intent

The server has one job: pull connections off a listener and drive each one
through the middleware pipeline, forever, without letting any single connection —
well-behaved or hostile — degrade the others or take down the host.

Three properties fall out of that intent and shape the whole implementation:

- **Connections are independent.** One connection's pace, idleness, or failure
  must never be observable by another. This rules out any design that serves
  connections from a shared loop.
- **Application faults are contained.** Middleware is arbitrary user code. A
  throw from it is expected, not exceptional, and must cost exactly one
  connection — never the accept loop, never the process.
- **Shutdown is deterministic.** Stopping the server drains what is in flight and
  releases every resource, without leaving an unobserved exception behind.

## Server dispatch model

```
StartAsync ──> AcceptLoopAsync (one stored Task)
                  │  (optional) await a concurrency slot
                  ├─ await listener.AcceptOrListenAsync
                  └─ dispatch ─> ServeConnectionAsync (one tracked Task per connection)
                                    await using connection
                                      OpenAsync
                                      await foreach ReceiveAsync
                                        pipeline.ExecuteAsync ─> SendAsync ─> dispose exchange
                                      dispose context
                                    (connection disposed by await using)
```

**One accept loop, one task per connection.** `StartAsync` schedules a single
accept loop as a stored `Task` and returns immediately (the host-service contract
uses the start token only to abort startup, which completes synchronously here).
The loop accepts a connection and *hands it off* to `ServeConnectionAsync` on its
own `Task`, then loops straight back to accept the next one. The loop never awaits
a connection's service.

**Why this is the whole point.** The previous implementation queued one
`async void` thread-pool work item that accepted a connection and then
`await foreach`-ed its entire receive sequence inline before accepting the next.
A single idle HTTP/1.1 keep-alive client — parked in `ReceiveAsync` waiting for a
request it never sends — blocked that loop indefinitely, so every other accepted
connection sat unserved in the listener backlog. Per-connection dispatch removes
the shared bottleneck: the idle client parks on *its* task while every other task
runs.

**Stored `Task`, never `async void`.** The accept loop and each connection task
are stored/tracked `Task`s. An `async void` body escalates any escaped exception
to a process-terminating unhandled exception via the thread pool; a `Task` makes
the exception observable instead. The accept loop additionally swallows its own
terminal faults so the stored task always completes cleanly.

**In-flight tracking.** Connection tasks are registered in a
`ConcurrentDictionary<long, Task>` keyed by a monotonic id, added by the accept
loop and removed by each task as it completes. The map is the drain set
`StopAsync` awaits. A connection that completes synchronously (only possible with
a degenerate/empty receive sequence) is removed immediately after registration so
the map never leaks a completed entry.

## Layering boundary — what the server does *not* do

Wire-level failure isolation lives one layer down, in
`Assimalign.Cohesion.Http.Connections` (see its `docs/DESIGN.md`,
"Receive-loop failure isolation"). Truncated frames, malformed request lines,
peer resets, HTTP/2 `RST_STREAM`/`GOAWAY`, and per-stream HTTP/3 faults are all
classified and handled there: the receive enumerable simply stops yielding on a
wire error and the surrounding `await using` disposes the connection.

The server therefore owns **only** the concerns above that layer:

| Concern | Owner |
| --- | --- |
| Wire-protocol conformance, frame parsing, per-stream reset | `Http.Connections` |
| Wire-level failure isolation (bad frames, peer reset) | `Http.Connections` |
| Application-exception isolation (middleware throws) | **this server** |
| Per-connection dispatch / concurrency | **this server** |
| Connection + context disposal | **this server** |
| In-flight tracking + graceful drain | **this server** |
| Optional concurrency cap | **this server** |

The server never inspects a frame, a stream id, or a protocol version. It sees
`IHttpConnection` → `IHttpConnectionContext` → `IHttpContext` and nothing lower.

## Error model — application-exception isolation

Each connection's receive loop wraps pipeline execution in a boundary that
catches exceptions by scope:

- **`OperationCanceledException`** — cooperative shutdown or a per-exchange
  cancellation. Treated as a clean drain, not a fault: the loop unwinds quietly.
- **Any other `Exception`** — an application fault from the middleware pipeline
  (or the per-exchange receive/send). The connection is `Abort`-ed so the peer
  sees it torn down, and the enclosing `await using` disposes it. The accept loop
  is untouched and keeps serving; a subsequently accepted connection is served
  normally.

Catching bare `Exception` here is a deliberate, documented departure from the
"catch specific exceptions" rule. This is a **fault-isolation boundary around
arbitrary user code**, the same pattern the transport's accept loop uses
(`HttpConnectionListener.RunStreamAcceptLoopAsync`). The alternative — letting an
unknown middleware exception propagate out of a background task — is precisely the
process-crash hazard this component exists to remove. The catch is annotated in
source so future readers do not "correct" it back to a narrow catch.

## Disposal contract

When a connection's loop ends — normally, by client disconnect, or by pipeline
fault — the server disposes, in order:

1. **Each exchange** (`IHttpContext`) in a `finally` around its pipeline
   execute/send, so an exchange is released even if the pipeline throws.
2. **The connection context** (`IHttpConnectionContext`) in a `finally` after the
   receive loop.
3. **The connection** (`IHttpConnection`) via `await using`.

`IHttpConnectionContext` is intentionally *not* `IAsyncDisposable`: a context is a
projection over the connection, and the connection releases the underlying
transport on its own disposal (see `Http1Connection.DisposeAsync`). The server
still disposes any context that *does* implement `IAsyncDisposable`/`IDisposable`
through a type test (no reflection), so a future stateful context is torn down
deterministically. Today this is a defensive no-op for the real projection
contexts; it exists so the disposal guarantee holds regardless of a context's
internal state.

## Stop semantics

`StopAsync` performs a graceful, idempotent shutdown:

1. **Signal shutdown.** Cancel the single shutdown `CancellationTokenSource`. This
   both stops the accept loop and unblocks every in-flight connection — an idle
   keep-alive parked in `ReceiveAsync` observes the cancellation and unwinds, so
   the drain cannot hang on it.
2. **Wait for the accept loop.** Await the accept-loop task first, so no new
   connection task can be added after the in-flight set is snapshotted.
3. **Drain in-flight connections.** `await Task.WhenAll` over the tracked
   connection tasks. Each task is self-contained — it swallows its own
   cancellation and faults and never rethrows — so the drain completes without
   surfacing an unobserved `OperationCanceledException` or any other escaped
   exception.
4. **Dispose the listener**, then the shutdown token source and (if present) the
   concurrency semaphore.

Cancelling before starting, or stopping twice, is a safe no-op guarded by
interlocked flags. The drain budget is owned by the caller's host lifecycle
(`Host<TContext>.StopAsync` applies `ShutdownTimeout`); the server does not
impose its own.

**Cancellation is drain, not force-kill of in-progress requests.** A single token
governs both "stop accepting" and "unblock in-flight connections." Idle
keep-alives unblock immediately; a request actively executing in the pipeline
observes the same cancellation and unwinds. Letting an in-progress request run to
completion before closing its connection (lame-duck draining) is a deliberate
non-goal for this iteration — see below.

## Concurrency cap (`MaxConcurrentConnections`)

Optional, configured builder-time via
`WebApplicationServerBuilder.LimitConcurrentConnections(int)` and carried on
`WebApplicationServerOptions.MaxConcurrentConnections`. `null` (the default) means
**unlimited**.

When set to a positive `N`, a `SemaphoreSlim(N, N)` gates the accept loop: a slot
is acquired **before** accepting a connection and released when that connection's
task finishes. Once `N` connections are being served, the loop stops accepting, so
additional connections stay in the listener's backlog channel — accepted by the
transport but not opened or served — applying natural backpressure until an active
connection completes. A non-positive cap is rejected at construction.

The gate is chosen for AOT-safety: a semaphore, stored `Task`s, and a
`ConcurrentDictionary` — no reflection, no dynamic code.

## AOT posture

`IsAotCompatible=true` holds with no special handling. The dispatch machinery is
`SemaphoreSlim`, `CancellationTokenSource`, `ConcurrentDictionary`, `Interlocked`,
`Task`, and `await using`/`await foreach` — all trim/AOT-clean. The defensive
context disposal is a `switch` type test, not a reflection probe. No runtime code
generation, no `Assembly.LoadFrom`, no reflection-based serialization.

## Testing

Behaviour is verified in `tests/` with xUnit + Shouldly against instrumented
doubles (`FakeHttpConnectionListener`, `FakeHttpConnection`,
`FakeHttpConnectionContext`, `FakeHttpContext`, `FakePipeline`) that let a test
script receive sequences, park connections, throw from the pipeline, and observe
opens/sends/aborts/disposals. The suite pins each acceptance property: idle
keep-alive non-starvation, single-connection fault isolation with continued
service, connection+context disposal on every exit path, graceful `StopAsync`
drain + listener disposal, and the concurrency cap holding connections back until
a slot frees.

## Non-goals

- **Lame-duck request draining.** Waiting for in-progress exchanges to finish
  before cancelling on shutdown (versus cancelling them with the drain token) is
  future work; it needs a two-phase signal ("finish the current exchange, accept
  no new ones on this connection") that this iteration does not implement.
- **HTTP/3 registration surface** on the server builder — tracked separately
  (issue #767). The TLS convenience surface (issue #763) has landed; see "TLS
  convenience surface" below.
- **Per-request service resolution.** DI/logging/config are builder-time only;
  the server resolves nothing per connection or per request.
- **Re-implementing wire behaviour.** Protocol conformance and wire-level failure
  isolation stay in `Http.Connections` and are never duplicated here.
The Web resource's composition root: the `WebApplicationBuilder` /
`WebApplication` surface that wires the `Assimalign.Cohesion.Http.Connections`
transport, the request pipeline, DI, logging, and configuration into a runnable
host. Per the repo's hosting philosophy, **this is the one place DI / Logging /
Config integration happens** — the transport and protocol libraries stay free of
those concerns.

This document grows as areas are touched rather than re-documenting the whole
surface at once. The broader server/runtime shape (per-connection dispatch,
error isolation, graceful stop) is being reworked under issue #762; this file
currently captures only the design decisions that are settled.

## Configuration-bound server limits and endpoints

### What it is

`WebApplicationServerBuilder.UseConfiguration(IConfiguration, sectionKey = "Http")`
(an extension member in `WebHostingExtensions`) binds the server's listener
**endpoints** and **server limits** from a Cohesion `IConfiguration` section at
builder time, giving `appsettings`-style Kestrel-section parity:

```json
"Http": {
  "Endpoints": {
    "Primary": { "Protocol": "Http1", "Host": "localhost", "Port": 8080 }
  },
  "Limits": {
    "MaxRequestLineSize": 8192,
    "MaxRequestBodySize": 30000000,
    "KeepAliveTimeout": "00:02:10",
    "RequestHeadersTimeout": "00:00:30"
  }
}
```

The actual binding lives in the internal `HttpServerConfiguration.Bind`, invoked
from inside the `UseServer((serviceProvider, options) => …)` callback so it runs
when the `HttpConnectionListener` is composed. Limits are per HTTP version on
the transport, so the single `Limits` section is parsed eagerly (an unparseable
value fails loudly even with no endpoints) into an
`Http1ConnectionListenerOptions.Http1Limits` template, and each endpoint the
section registers copies the bound values into its own per-registration limits
through the transport's `UseHttp1` / `UseHttp2` configure overloads — HTTP/1.1
endpoints receive every key; HTTP/2 endpoints receive the shared
`HttpConnectionListenerLimits` keys (`MaxRequestBodySize`, `KeepAliveTimeout`,
`RequestHeadersTimeout`), because the HTTP/1.1 wire-format keys have no HTTP/2
meaning. The HTTP/2 abuse caps (`Http2ConnectionListenerOptions.Http2Limits`)
are not yet config-bindable — a `Limits:Http2` section is a natural follow-up
when a deployment needs it.

### Why explicit, hand-rolled binding

The binding is deliberately **not** reflection-based:

- **AOT is a hard repo requirement.** A reflection binder (`Bind<T>` over
  property setters) is exactly the pattern trimming and NativeAOT break. Every
  value is instead read by its known path (`GetValue`) and parsed with the
  invariant culture. There is no dynamic member discovery and no
  `Microsoft.Extensions.*` dependency.
- **Security config must fail loudly.** A mistyped limit is a security
  regression waiting to happen (`MaxRequestBodySize: "1O485760"` silently
  falling back to a default would be dangerous). A value that is *present but
  unparseable* throws; a value that is *absent* leaves the built-in
  Kestrel-parity default in place. The two cases are never conflated.

### Binding specifics

- **Body size.** `"unbounded"` / `"none"` bind to `null` (no cap); otherwise a
  non-negative integer.
- **Timeouts.** A `TimeSpan` string (`"00:00:30"`), a bare whole number of
  seconds, or `"infinite"` / `"-1"` for `Timeout.InfiniteTimeSpan`.
- **Endpoint host.** Literal IP addresses, `localhost` (→ loopback), and the
  wildcards `*` / `+` / `0.0.0.0` (→ any) / `[::]` / `::` (→ IPv6 any). DNS is
  **not** resolved at bind time — a hostname that is not one of those is an
  error, because binding-time DNS is an I/O surprise the composition root should
  not hide.
- **Endpoint protocol.** `Http1` (default) or `Http2`; anything else throws.

### Scope boundary

`UseConfiguration` binds endpoints and the HTTP/1.1 server limits only. TLS
composition, HTTP/3 registration, and the connection-dispatch rewrite are
separate concerns (the latter under #762). Data-rate limits are deferred with
the transport's streaming-body rework.

### AOT posture

No reflection, no codegen, no dynamic activation. The binder is straight-line
`GetValue` / `TryParse` calls; endpoints are wired through the already-AOT-safe
TCP convenience overloads.

## Default request-parse interceptors

### What it is

When the web host composes the `HttpConnectionListener`, it installs the
default request-parse interceptors **before** any user `UseServer`
configuration runs (`WebApplicationServerBuilder.ApplyDefaultInterceptors`).
Today that is one interceptor: `Http.RequestLimits`'
max-request-body-size interceptor, which occupies slot 0 of the interceptor
order so every request carries the typed `IHttpMaxRequestBodySizeFeature` and
user-registered interceptors' `AfterRequestHead` hooks can observe it. As of #819 the seam is
invoked on **all three** parse paths — HTTP/1.1, HTTP/2, and HTTP/3 — so the
feature is attached uniformly regardless of protocol. Cap *enforcement* (the
413) is still HTTP/1.1-only: h2 bounds body buffering via flow-control
backpressure and h3 via QUIC flow control, so a lowered cap changes the
reported feature value but does not reject the body there yet (the hard cap is
tracked in the transport's protocol-coverage notes and on
`HttpConnectionListenerLimits.MaxRequestBodySize`).

### Why default-on, and why here

The transport itself stays lean — with zero interceptors it allocates no
per-request interception state at all — so the "every request always has the
typed feature" guarantee is a *hosting* policy, not a transport one. It lives
here because this is the composition root: apps that want a leaner pipeline can
inspect or clear `HttpConnectionListenerOptions.Interceptors` in their own
`UseServer` callback (user configurations run after the defaults), which keeps
the default overridable without a dedicated opt-out knob.

### Non-goals

No other interceptor ships by default. Parse-time features under design
(digest fields, request decompression) register through the same seam when
their packages land, but each is an explicit opt-in.

## Pipeline terminal — the 404 fallback

### What it is

`WebApplication`'s pipeline builder terminates the middleware chain with
`TerminalAsync` instead of the former silent `_ => Task.CompletedTask`. Reaching
the terminal means every registered middleware chained to `next` and none
produced a terminal response — the request went unhandled. Rather than hand the
transport an empty 200, the terminal writes a **404 `application/problem+json`**
body via `IHttpResponse.WriteProblemDetailsAsync` (from
`Assimalign.Cohesion.Web.Results`, issue #776).

### Why here, and the no-clobber guard

The terminal is baked into `WebApplication.Build` because it is the one middleware
the host always installs; it is the always-on "nothing matched" floor. It is
distinct from the opt-in `UseExceptionHandler()` boundary (which converts *thrown*
faults into a 500): the terminal handles the *silent fall-through* case, the
boundary handles the *exception* case, and the two never overlap because a middleware
that throws never reaches the terminal.

The terminal only writes when the response is genuinely untouched — status still
200, no `Content-Type`, no redirect `Location`, and an empty (seekable) body. A
middleware that already chose a non-200 status, wrote a body, or set a redirect is
left exactly as it was, so the fallback never clobbers a deliberate empty-bodied
response (a 204, a 302, a `HEAD` 200). This heuristic stands in for a response
`HasStarted` flag the message model does not yet expose; when routing lands
(#150/#28) the "unhandled" signal becomes explicit (no endpoint matched) and the
terminal can rely on that instead of inferring it.

### Dependency direction

`Web.Hosting` takes a `CohesionProjectReference` on `Web.Results` for the
`ProblemDetails` model and the problem+json writer. The direction is one-way
(`Web.Hosting → Web.Results`); the middleware and their `UseXxx()` registration
verbs live in `Web.Results` and never reference the host. See
`Assimalign.Cohesion.Web.Results/docs/DESIGN.md` for the boundary/ProblemDetails
design and the placement rationale.

## TLS convenience surface

### What it is

`HttpConnectionListenerOptions.UseHttp1s(configure, tlsOptions)` and
`UseHttp2s(configure, tlsOptions)` (extension members in `WebHostingExtensions`)
are the secure siblings of the plaintext `UseHttp1` / `UseHttp2` callback sugar.
Each takes the same `Action<TcpConnectionListenerOptions>` used to configure the
endpoint plus a `TlsServerOptions`, and registers a listener that serves the
protocol over TLS:

```csharp
builder.Server.UseServer(options =>
{
    options.UseHttp2s(
        tcp => tcp.EndPoint = new IPEndPoint(IPAddress.Loopback, 8443),
        new TlsServerOptions
        {
            AuthenticationOptions = { ServerCertificate = certificate }
        });
});
```

### Why here, and why compose-before-register

TLS is a **pre-composed transport layer**, never an HTTP concern — the
`Assimalign.Cohesion.Http.Connections` `docs/DESIGN.md` records this boundary
("TLS is a pre-composed layer, not an HTTP concern"), and its
`HttpConnectionListenerOptions` deliberately carries no TLS or certificate
options. The convenience honors that boundary by composing
`TcpConnectionListener.Create(configure).UseTls(tlsOptions)` **before** handing
the listener to `UseHttp1` / `UseHttp2`. Composition is deferred inside the same
factory the plaintext sugar uses, so the TCP listener is not bound until the
`HttpConnectionListener` materializes the registration.

Because the security layer wraps the listener first, the layered listener reports
`Capabilities.Security == ConnectionSecurity.Tls`. That capability is the single
source of truth for the `https` scheme — the HTTP layer reads it once per accept
loop; there is no registration-time `isSecure` parameter to thread through. A
request served over a `UseHttp1s` / `UseHttp2s` listener therefore carries
`HttpScheme.Https` end to end.

This is also the layering reason the surface lives in Web.Hosting rather than in
the transport: Web.Hosting is where the composition root is allowed to depend on
both `Http.Connections` (the registration surface) and
`Connections.Security` (the `UseTls` layer). The transport depends on neither
direction of that composition.

### ALPN defaulting

The .NET / browser HTTP client selects the HTTP version over TLS via ALPN
(RFC 7301), so a secured HTTP/2 listener is only reachable as HTTP/2 if it
advertises the `h2` protocol id. To make the common case work without ceremony,
`UseHttp2s` defaults `AuthenticationOptions.ApplicationProtocols` to
`SslApplicationProtocol.Http2` (`h2`) and `UseHttp1s` defaults it to
`SslApplicationProtocol.Http11` (`http/1.1`) **when the caller left the list
unset** (null or empty). A caller who supplies an explicit protocol list — for
example to offer both `h2` and `http/1.1` on one endpoint — has it preserved
unmodified. The default is written onto the caller's `TlsServerOptions` (an
intentional mutation) so a later read observes the negotiated protocol.

### Certificates are the caller's concern

The server certificate is supplied by the caller through
`TlsServerOptions.AuthenticationOptions.ServerCertificate` (or a selection
callback). Certificate sourcing, storage, and rotation are Security-area concerns
and are explicit non-goals of the security library's TLS surface, so they are not
re-modeled on this convenience.

### Scope boundary

`UseHttp1s` / `UseHttp2s` cover the stream protocols. HTTP/3 is out of scope here:
QUIC's transport security is always-on (TLS is inherent to the protocol) and QUIC
listeners bind asynchronously, so the HTTP/3 registration surface is tracked
separately under #767.

### AOT posture

No reflection, no runtime codegen. The composition is plain delegate wiring
(`TcpConnectionListener.Create(...).UseTls(...)` inside a `Func<IConnectionListener>`),
and the ALPN default is a list assignment. `IsAotCompatible=true` holds with no
special handling.
