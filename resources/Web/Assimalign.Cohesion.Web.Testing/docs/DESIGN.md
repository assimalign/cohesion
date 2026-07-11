# Assimalign.Cohesion.Web.Testing — Design

## Design intent

A code-first multi-service framework whose flagship resource is a web server cannot ship
without socketless full-pipeline testing: it is how every consumer will integration-test
their services, and how Cohesion itself tests h1/h2 pipeline semantics deterministically in
CI across three operating systems. `WebApplicationTestFactory` is that surface — the Cohesion
analogue of ASP.NET's `WebApplicationFactory` / `TestServer`, built instead from two seams
the framework already ships:

- **Server side** — the builder-time listener registration seam:
  `builder.Server.UseServer(options => options.UseHttp1(listener))` accepts any
  `IConnectionListener`, so the in-memory driver's `InMemoryConnectionListener`
  (`Assimalign.Cohesion.Connections.InMemory`, #772) plugs in with no contract changes.
- **Client side** — `SocketsHttpHandler.ConnectCallback`: the real .NET HTTP client dials the
  in-memory listener's bound `InMemoryConnectionFactory` and speaks over the returned
  duplex-pipe stream.

Nothing in the middle is faked. A test request crosses the real HTTP/1.1 or HTTP/2 wire
format, the real `HttpConnectionListener` receive loop, the real `WebApplicationServer`
per-connection dispatch (#762), and the application's real pipeline. What the factory removes
is only the operating-system socket — and with it, port allocation, loopback flakiness, and
cross-OS nondeterminism.

## Why the real `SocketsHttpHandler`, not a synthetic message handler

The alternative — a custom `HttpMessageHandler` that invokes the pipeline in process, the
`TestServer` model — is cheaper per request but skips the transport entirely: no request
serialization, no header wire casing, no keep-alive/pooling behaviour, no h2 framing or
stream multiplexing, no connection lifecycle. Those layers are exactly where Cohesion's own
bugs would live (the transport and server are first-party code here, unlike ASP.NET's
battle-tested Kestrel), so the factory deliberately buys wire fidelity: the bytes a test
exercises are the bytes production exercises. The trade-off accepted is a real client
connection pool in each test — which the isolation model below turns into a feature.

## Composition and drive model

```
WebApplicationTestFactory
 ├─ owns InMemoryConnectionListener + its bound InMemoryConnectionFactory
 ├─ Builder  (WebApplication.CreateBuilder(); ctor registers UseServer → UseHttp1/UseHttp2(listener))
 ├─ Application  (built lazily on first access; pipeline configured here)
 └─ CreateClient()
      └─ SocketsHttpHandler.ConnectCallback ──dials──► factory ──► ClientConnectionStream
                                                                        (owns the Connection)
```

**Two-phase configuration, snapshot at start.** Services and features are configured on
`Builder` before the application is built; the pipeline (`Use`, `UseRouting`, `Map`) is
configured on `Application` after. When the factory starts it resolves the default
`IWebApplicationServer` from the application's service provider, which materializes the
pipeline snapshot — pipeline mutations after start are not observed. This mirrors the
production composition order rather than inventing a test-only one.

**The factory drives the server, not the host.** Starting resolves and starts the default
`IWebApplicationServer` directly — the same documented drive path the Web.Hosting test suite
uses — rather than running the whole `Host` lifecycle. The default server's registration is
`IWebApplicationServer`-keyed (it is not among the host's `IHostService`s today), and host
lifecycle orchestration is the hosting epic's concern (#26); the factory stays scoped to
"serve requests against the pipeline". User-registered `IHostService`s are not started by
the factory — a deliberate non-goal below.

**Start-on-first-client.** `CreateClient()` starts the factory when it has not been started
yet (ASP.NET `WebApplicationFactory.CreateClient` parity). The blocking wait inside is safe
by construction: the default server's `StartAsync` only schedules its accept loop and
completes synchronously. `StartAsync`/`StopAsync` remain public for tests that assert
lifecycle behaviour itself.

## Client connection ownership

`ConnectCallback` returns a `ClientConnectionStream` — the connection's duplex-pipe stream
adapter plus one added responsibility: disposing the stream disposes the dialed
`Connection`. `SocketsHttpHandler` disposes a pooled connection's stream when it evicts or
tears down the connection; without the ownership hook the client end's pipes would never
complete and the server end would stay parked in its receive loop until server shutdown.
With it, client-side teardown propagates as end-of-stream to the server exactly as a closed
socket would.

## Protocol scope

- **HTTP/1.1** (default) — `UseHttp1(listener)`; clients speak plain 1.1 with keep-alive and
  pooling.
- **HTTP/2, prior knowledge (h2c)** — `UseHttp2(listener)` server-side; clients pin
  `DefaultRequestVersion = 2.0` with `HttpVersionPolicy.RequestVersionExact`, which makes
  `SocketsHttpHandler` speak h2 from the first byte over the plaintext stream — no TLS, no
  ALPN, no Upgrade dance. Streams multiplex over the single in-memory duplex pair; the test
  suite pins that concurrent requests share one connection.
- **HTTP/3 — out of scope.** h3 is QUIC-bound end to end: `UseHttp3` takes an
  `IMultiplexedConnectionListener` (the driver's multiplexed variant could serve it), but
  `SocketsHttpHandler` offers no client seam to substitute an in-memory multiplexed
  transport — `ConnectCallback` is a stream seam, and the client's h3 stack rides real QUIC.
  A meaningful h3 test surface therefore needs a QUIC-over-memory story (and likely a
  Cohesion-native h3 test client), tracked alongside the HTTP/3 registration surface (#767).
  Until then, h3 wire behaviour stays covered by the transport's own protocol tests.

TLS-over-memory is likewise not modeled: the security library's `UseTls` layer can compose
over an in-memory pair as over any transport, but certificate-trusting client plumbing adds
ceremony the socketless factory exists to remove. Scheme-dependent behaviour is better
tested at the unit level or over the loopback TLS integration tests that already exist in
Web.Hosting.

## Lifecycle contract

| Phase | What happens |
| --- | --- |
| Construct | Listener + dial factory created; `Builder` prepared with the in-memory `UseServer` registration. Nothing runs. |
| `Application` access | `Builder.Build()` (once, thread-safe). |
| `StartAsync` / first `CreateClient` | Default server resolved (pipeline snapshot) and started; accept loop live. Idempotent. |
| `StopAsync` | Server's graceful stop: stop accepting, drain in-flight connections, dispose the listener chain. New dials are refused (`ConnectionAbortedException` → client `HttpRequestException`). Idempotent; no-op before start. |
| `DisposeAsync` | `StopAsync`, then application disposal, then defensive in-memory listener teardown (idempotent for the never-started factory). Safe to call twice. |

Stop semantics — including cancellation-as-drain for in-flight exchanges — are owned and
documented by Web.Hosting (`docs/DESIGN.md`, "Stop semantics"); the factory adds no policy of
its own on top.

## Parallel test isolation

Each factory owns a private listener, dial factory, and application. Because the router
builder is per-application state (#789 — `AddRouting` registers a per-application
`IRouterFeature`, and `UseRouting` resolves that same feature), two factories in one process
share no route tables, middleware, or connections. The test suite guards this end to end:
two live factories with disjoint route maps serve their own routes and 404 each other's,
sequentially and concurrently. This is what makes the factory safe under parallel xUnit
execution — the intended usage, not an edge case.

## AOT posture

`IsAotCompatible=true` holds with no special handling and **zero reflection**: composition is
plain delegate wiring over the builder seams, the client is BCL `SocketsHttpHandler` +
`HttpClient`, and the connection stream is a pipe adapter. No runtime code generation, no
`Assembly.LoadFrom`, no reflection-based serialization — unlike ASP.NET's
`WebApplicationFactory<TEntryPoint>`, there is no entry-point discovery via reflection;
composition is explicit on the factory's `Builder`/`Application`.

## Non-goals

- **HTTP/3 / QUIC-over-memory** — see "Protocol scope" above; tracked with #767.
- **Full host lifecycle orchestration.** The factory starts the default web server only; it
  does not run `IHost.StartAsync` or user-registered `IHostService`s. When the hosting
  integration epic (#26) lands a canonical "run the whole host" path, the factory can grow
  an opt-in for it.
- **TLS composition over the in-memory pair** — compose `Connections.Security` directly in a
  dedicated test if ever needed; the factory stays plaintext.
- **Assertion/fixture surface.** No response-assertion helpers, no xUnit fixtures — hosting
  only, so the package stays framework-neutral.
- **Client tracking.** The caller owns clients from `CreateClient` and disposes them;
  factory disposal tears down the server side regardless, so a leaked client cannot leak a
  server-side connection past the drain.

## Relationships

- **`Assimalign.Cohesion.Connections.InMemory`** — the transport this factory rides; its
  `docs/DESIGN.md` records the pair wiring, teardown semantics, and dial/accept model.
- **`Assimalign.Cohesion.Web.Hosting`** — the composition root and server whose lifecycle the
  factory manages; its `docs/DESIGN.md` owns the dispatch and stop semantics the factory's
  drain relies on. Web.Hosting's integration test suite consumes this package for its
  middleware-ordering, per-connection-concurrency, and graceful-shutdown coverage.
  This reference is the **sanctioned exception** to the Web-area dependency rule
  (`resources/Web/README.md`): no other Web library may reference the hosting module, but the
  test factory exists precisely to drive the concrete runtime
  (`WebApplication`/`WebApplicationBuilder`), which cannot be done through abstractions alone.
- **`Assimalign.Cohesion.Web.Routing`** — per-application router state (#789) is what makes
  the parallel-isolation guarantee hold; the isolation regression tests live here.
