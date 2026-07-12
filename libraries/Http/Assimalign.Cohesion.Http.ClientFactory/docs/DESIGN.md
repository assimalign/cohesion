# Assimalign.Cohesion.Http.ClientFactory — Design

## Design intent

Solve the classic `HttpClient` lifetime trade-off once, in one place: callers may freely
`using`-dispose the clients they receive (no ephemeral-port exhaustion), while the underlying
`SocketsHttpHandler` — and therefore the connection pool and DNS/TLS session state — is shared
per name and refreshed on a bounded rotation. The factory is deliberately a small, DI-free
builder + factory pair: composition happens at build time on `HttpClientFactoryBuilder`, and the
`*.Hosting` layer (not this library) decides how the factory is surfaced to applications.

## Why this shape and not another

- **Rotating handler pool, not per-call handlers.** A handler per `Create` call exhausts
  sockets; a process-lifetime singleton pins DNS results and TLS sessions forever. The pool
  gives each name one active handler inside a lifetime window (`HandlerLifetime`, default two
  minutes): every `Create` inside the window reuses it, and rotation bounds how stale
  resolution state can get. Rotation is compare-and-swap over a `ConcurrentDictionary` with a
  per-name lock so handler construction is single-flight under load.
- **`LifetimeTrackingHttpMessageHandler` wrappers, not reference counting.** Each returned
  `HttpClient` owns a lightweight wrapper (`disposeHandler: true`) that does *not* propagate
  disposal to the shared inner handler. Expired handlers are kept on an expired list and
  disposed only when garbage collection proves no wrapper (and so no client) still references
  them — no manual release call for consumers to forget.
- **Builder + options objects, not a DI container.** Named registration is a dictionary of
  `NamedHttpClientOptions` captured at `Build()`. This matches the repo-wide rule that only
  `*.Hosting` modules integrate DI; everything here is plain values and delegates, AOT-safe.

## Redirect policy is factory-owned (RFC 10008 § 2.5)

Automatic redirect following lives in the factory's own `RedirectHttpMessageHandler`
(a `DelegatingHandler` wrapped around the pooled inner handler), not in
`SocketsHttpHandler.AllowAutoRedirect` — which the factory always forces **off** so exactly one
layer acts.

**Why own it rather than delegate to the BCL:**

- **The method semantics are load-bearing.** RFC 10008 § 2.5 requires that a redirected QUERY
  be re-issued as a QUERY with its original content on `301`/`302`/`307`/`308` — never silently
  rewritten to GET — while § 2.5.3 sanctions exactly one method switch: `303 See Other` is
  fulfilled with a GET on the Location URI. Owning the layer makes those semantics an explicit,
  tested contract of this library rather than an implementation detail of whatever inner
  handler happens to be pooled.
- **Uniformity across inner handlers.** `HandlerFactory`-injected handlers (tests, custom
  stacks) get the same redirect behavior as the default `SocketsHttpHandler` path, and the
  policy is unit-testable with a scripted terminal handler — no wire, no sockets.
- **One switch.** `NamedHttpClientOptions.AllowAutoRedirect` / `MaxAutomaticRedirections` are
  the only redirect knobs. Configuring redirects through `ConfigureHandler` is documented as
  ineffective (the inner value is forced off after the callback runs) — the alternative, honoring
  the inner setting, would mean two layers that can silently double-follow.

**The rules the handler implements** (mirroring RFC 9110 § 15.4 and the BCL's safety posture):

| Status | Behavior |
|---|---|
| `301` / `302` | Re-issue with method + content preserved; the historical POST→GET rewrite is preserved for POST only |
| `303` | Re-issue as GET, content dropped (the one sanctioned switch — RFC 10008 § 2.5.3) |
| `307` / `308` | Re-issue with method + content preserved (the statuses forbid rewriting) |
| `300` | Never followed — choosing among alternatives is the caller's decision |

Plus: relative `Location` values resolve against the current request URI; an `https`→`http`
downgrade is never followed (the raw `3xx` surfaces); the `Authorization` field is dropped on
every hop; exceeding `MaxAutomaticRedirections` stops following and returns the last `3xx`;
intermediate responses are disposed.

**Accepted constraint:** a followed redirect re-serializes the same `HttpContent` instance, so
buffered contents (`ByteArrayContent`, `StringContent`, `JsonContent`) ride redirects while
one-shot contents (`StreamContent` over a non-seekable stream) cannot — the same constraint the
BCL's built-in redirect handling carries. The factory does not buffer request bodies on the
caller's behalf.

## Lifecycle pattern

The factory is `IDisposable`/`IAsyncDisposable` and owns every pooled handler: disposal at
application shutdown disposes the active and expired handlers. The redirect wrapper participates
naturally — `DelegatingHandler.Dispose` propagates to the inner handler it wraps, and the
per-client lifetime-tracking wrapper never propagates disposal inward.

## Error model

No library-specific exception types. Misuse throws BCL exceptions from the guard clauses
(`ArgumentException`, `InvalidOperationException`, `ObjectDisposedException`); wire and protocol
failures surface as the BCL's `HttpRequestException` family, untranslated.

## AOT posture

No reflection, no dynamic code, no serialization — plain delegates and BCL types end to end.

## Non-goals

- **No request resilience** (retries, circuit breaking, hedging): compose a policy handler via
  `HandlerFactory`/`ConfigureHandler`; the factory stays a lifetime manager.
- **No cookie-jar or cache management**: those belong to the inner handler's configuration.
- **No automatic `300 Multiple Choices` navigation** and **no fragment inheritance** on
  redirects — both are user-agent decisions, not client-library mechanics.
- **No DI surface**: hosting layers own how factories reach application code.
