# Assimalign.Cohesion.Web.Hosting — Design

`Assimalign.Cohesion.Web.Hosting` is the composition root for the Web resource: it
builds a `WebApplication` (a `Host<WebApplicationContext>`), wires the middleware
pipeline, and owns the runtime server that turns accepted HTTP connections into
pipeline invocations. It is the **only** Web layer where DI, logging, and
configuration are integrated, and that integration is strictly builder-time —
nothing resolves services per request.

**The hosting module is dependency-isolated within the Web area** (rule adopted
2026-07-10, recorded in `resources/Web/README.md`): no Web feature library
references this package — a feature that did would drag the DI/configuration
composition surface into every consumer — and this package references **no**
Web feature library, only the root `Assimalign.Cohesion.Web` abstractions and
non-Web infrastructure. Applications still see the whole Web family because the
`App.Web` shared framework (via `Sdk.Web`) delivers every Web assembly; builder
verbs ship with their features (`AddAuthentication` moved to
`Web.Authentication`, `AddCookie`/`AddJwtBearer` to their handler packages) and
compose against the root `IWebApplicationBuilder` seam. The one sanctioned
exception is `Web.Testing`, the harness that drives this concrete runtime.

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

## Application feature seeding

`IWebApplicationBuilder.AddFeature` registers `IHttpFeature` singletons (routing's
per-application `IRouterFeature` is the canonical example), but a feature is only
useful once it is present on each exchange's `IHttpContext.Features` collection.
That bridging happens when the pipeline is built: `WebApplication`'s pipeline
`Build()` resolves the registered features **once** and, when any exist, wraps the
composed pipeline in a seeding middleware that stamps each feature onto every
exchange before any user middleware runs.

Two deliberate properties:

- **Builder-time snapshot, not request-time service location.** The feature set is
  materialized at pipeline build (which happens when the server is resolved). Per
  the hosting philosophy, nothing resolves services per request — the per-exchange
  work is a plain array walk. Features registered after the pipeline is built are
  not observed, the same snapshot rule the middleware list follows.
- **Application-registered features are per-application.** Each application seeds
  only its own DI-registered features, which is half of the process-wide isolation
  story (#789's per-application router state is the other half).

## Host filtering (allowed hosts)

### What it is

Builder-time allowed-hosts enforcement (issue #781): a defense against
Host-header injection (cache poisoning, password-reset poisoning, absolute-URL
generation against an attacker-chosen host), which matters here because a
Cohesion web application is designed to be directly exposed by its own server,
not assumed to sit behind a validating proxy.

Configuration lives on `WebApplicationOptions.HostFiltering` (surfaced as
`WebApplicationBuilder.HostFiltering`): an `AllowedHosts` pattern list plus an
`AllowEmptyHost` policy flag. The matching primitives — the `host[:port]`
component split on `HttpHost` and the precompiled allowlist matcher
(`IHttpHostMatcher` / `HttpHostMatcher`) — live in `Assimalign.Cohesion.Http`
(see its `docs/DESIGN.md`, "Host values and allowlist matching"); this module
owns only the policy surface and the enforcement point. Per the issue and the
lean-dependency-tree rule there is deliberately **no `Web.HostFiltering`
micro-package**.

### Enforcement — first position, built once

While `AllowedHosts` is empty (the default) no middleware is installed and
every host is accepted — the opt-in mirrors the pre-#781 behavior and costs
nothing. When patterns are present, the pipeline `Build()` compiles them into a
matcher **once** and wraps the composed pipeline in the filtering middleware at
the **first position** — outside feature seeding and every user middleware — so
nothing downstream ever observes a request whose host failed validation, and a
rejected request does not even pay for feature stamping. Invalid patterns
(ports, wildcard misuse, malformed hosts, an empty-compiling allowlist) throw
`ArgumentException` from `Build()`: configuration errors surface at startup,
never as per-request behavior.

The check itself is builder-time-composed and request-time-trivial: the
transports already resolve the effective host with the correct per-version
precedence (HTTP/1.1 absolute/authority-form target supersedes `Host`
per RFC 9112 §3.2.2; HTTP/2 / HTTP/3 `:authority` via
`HttpFieldNormalization.ResolveAuthority`), so the middleware reads
`IHttpRequest.Host` and performs one component split plus span comparisons. A
mismatch answers `400 Bad Request` with an empty body (the HTTP/1.1 writer
synthesizes `Content-Length: 0`) and short-circuits; the connection itself is
left alive. A richer problem-details payload is deliberately not rendered here:
the runtime module must not depend on Web feature packages (`Web.ProblemDetails`
included) per the hosting-isolation rule, and rejected-request bodies are an
application error-handling concern (the #864 `OnError` direction), not a
hosting one.

**Empty/missing host policy (RFC 9112 §3.2).** An HTTP/1.1 request that lacks a
`Host` header (and carries no target authority) resolves to `HttpHost.Empty`
and cannot be validated, so with filtering active it is rejected by default.
`AllowEmptyHost = true` is the explicit opt-out for legacy HTTP/1.0-style
clients; it admits only the *hostless* case — a present-but-unmatched host is
still rejected.

### Validation, not selection — composing with #788

Host filtering **validates** the request ("is this host one of mine?");
routing's host constraints (#788, `RequireHost` / `RouteHostMetadata`)
**select** among endpoints ("which route serves this host?"). They are
complementary, not duplicates: both consume the same `HttpHost` component
semantics from the Http core (bracket-insensitive IPv6, case-insensitive,
apex-excluded `*.` wildcards), so a given wire value means the same thing on
both paths — but a filtering mismatch is a 400, while a routing host mismatch
merely skips a route candidate. Use filtering to bound the hosts the
application answers *at all*, and host-constrained routes to fan traffic
across the hosts inside that boundary.

### Ordering — forwarded headers (#778)

The filter validates `IHttpRequest.Host` *as it is when the filter runs*, and
the filter runs first. When the application trusts a fronting proxy that
conveys the public host in `X-Forwarded-Host` / `Forwarded: host=…` (#778,
in flight), the wire-level `Host` reaching this server is the proxy's target —
typically the internal name the proxy dialed. Until the forwarded-headers
middleware lands and its trust model defines where its rewrite happens, the
composition contract is:

- **Allowlist what actually arrives on the wire.** Behind a trusted proxy that
  means the internal/edge name(s) the proxy uses — the forwarded *public* host
  is then #778's to validate under its own trust model (mirroring ASP.NET's
  split, where `ForwardedHeadersOptions.AllowedHosts` bounds `X-Forwarded-Host`
  separately from host filtering).
- If #778's design instead rewrites `Host` ahead of validation (its middleware
  would have to be hosting-coordinated in front of this filter — the seam
  exists, since hosting owns the pipeline head), the allowlist then names the
  public hosts. That decision belongs to #778; this module deliberately does
  not pre-wire it.

Either way the two stay coordinated through this section and #778's design
notes — the filter itself needs no knowledge of forwarding.

### Scope boundary

- The guard protects the pipeline the host composes. An application that
  replaces the pipeline wholesale via `IWebApplicationBuilder.AddPipeline`
  owns its own composition, including host validation.
- Enforcement is per-application (each application compiles its own matcher
  from its own options), consistent with the per-application feature story
  above.

### AOT posture

Options → precompiled matcher at build; request-time span comparisons only. No
reflection, no configuration binding, no service location.

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

The same properties are additionally pinned **end to end** by the full-pipeline
integration suite (`WebApplicationPipelineIntegrationTests`,
`WebApplicationServerIntegrationTests`), which drives the real server over the
in-memory transport through `Assimalign.Cohesion.Web.Testing`'s
`WebApplicationTestFactory` — a real `HttpClient`, real HTTP/1.1 exchanges, no
sockets. It covers middleware onion ordering and short-circuiting, per-connection
dispatch (a parked connection does not starve others), application-fault isolation
with continued service, and graceful shutdown draining (in-flight unwind, idle
keep-alive unblock, post-stop connection refusal).

Host filtering has its own end-to-end suite (`HostFilteringTests`): allow/deny
over origin-form HTTP/1.1 and HTTP/2 `:authority` through the factory, plus raw
HTTP/1.1 exchanges over the in-memory transport for the cases `HttpClient`
cannot produce — a missing `Host` header (the empty-host policy both ways) and
absolute-form request-targets superseding the `Host` header.

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
