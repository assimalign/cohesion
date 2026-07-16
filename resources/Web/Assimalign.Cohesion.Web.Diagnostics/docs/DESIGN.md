# Assimalign.Cohesion.Web.Diagnostics — Design

## Design intent

Access/request logging is table-stakes for a production web server: audit-grade request lines,
header capture with redaction, duration/status, and W3C log files existing tooling can ingest.
This package is the Web area's one diagnostics module (issue #794) — deliberately a single
project holding both the HTTP logging middleware and the W3C access-log writer, not
ASP.NET-style micro-packages.

The load-bearing decision is that **emission rides Cohesion's own Logging model**. The
middleware produces ordinary `LoggerEntry` values through the composed `ILogger`; the W3C
writer is an ordinary `ILoggerProvider` registered on the same `LoggerFactoryBuilder` as
console/debug sinks. Nothing here invents a second pipeline, and every existing logging
facility — provider fan-out, factory filter rules, enrichers, scopes — applies to access-log
entries for free.

## The attribute contract

The middleware and the provider are decoupled by the stable attribute names in
`HttpLoggingAttributes` (`http.request.method`, `http.response.status`, `http.duration`, ...),
aligned with OpenTelemetry HTTP semantic conventions where one exists but without any OTel
dependency. The provider renders **only** entries stamped `http.event = "exchange"` and ignores
everything else, so it is safe on a factory that also carries application logging. This is the
same seam a future OTLP/OpenTelemetry adapter would consume (#583/#317): correlation concepts
(trace/span ids as attributes) live here; OTel-specific behavior belongs in later adapters.

Trace correlation is the inbound `traceparent` header, span-parsed (`Internal/TraceParent`)
into `trace.id`/`span.id` attributes when valid. Optionally (`LogRequestStart`), a start entry
seeds an `IScopedLogger` scope so the completion entry correlates via `ILoggerEntry.ParentId` —
the Logging library's own correlation mechanism, not a bespoke one.

## Why-this-not-that

- **Explicit logger at composition time, not DI.** `UseHttpLogging(ILogger | ILoggerFactory, ...)`
  takes its dependency the way `MapHealthChecks(IHealthCheckService)` does: handed in when the
  pipeline is composed. The alternative — resolving `ILoggerFactory` from a container at request
  time — violates the repo's `*.Hosting`-only DI rule and is exactly the request-time service
  location the Web composition model forbids.
- **Options frozen into a snapshot at `Use` time.** `HttpLoggingOptions` is scratch space;
  `HttpLoggingSnapshot` (frozen sets, validated limits) is what the middleware holds. Mutating
  options after composition has no effect — the same parse-once discipline as the routing
  metadata carriers. There is no request-time configuration surface to misuse.
- **Allowlist redaction, not a denylist.** A denylist fails open: every new sensitive header
  leaks until someone remembers to add it. The allowlist fails closed — a header's value logs
  only when explicitly allowed, and `Authorization`/`Proxy-Authorization`/`Cookie`/`Set-Cookie`
  are simply never in the defaults. Header *names* still log, preserving diagnostic signal
  (presence/absence) without the values. `RequestQuery` is likewise excluded from
  `HttpLoggingFields.Default` — query strings carry tokens; ASP.NET made the same call.
- **Tee-capture streams, never buffering.** Body capture wraps the body streams and copies the
  first N bytes as they flow; the bytes themselves stream through untouched. h2 flow-control
  backpressure (#750), h1 data-rate gates (#810), and the request-size limits (#764/#818) all
  behave exactly as without the middleware. The alternative (buffer-then-log, ASP.NET's
  `EnableBuffering`) would hold entire bodies in memory and fight the transport's
  consumption-driven window updates.
- **Request-body interposition via the `HttpRequest` base, degrading gracefully.**
  `IHttpRequest.Body` is get-only, but the abstract `HttpRequest` base (which every transport
  derives from) has a settable `Body`. The middleware type-tests for the base; a hypothetical
  `IHttpRequest` that isn't an `HttpRequest` simply gets no request-body capture or count. The
  response side needs no such test — `IHttpResponse.Body` is settable by contract.
- **Response capture decided at first write.** The response `Content-Type` does not exist when
  the middleware runs, so the content-type gate for response capture is a predicate the wrapper
  evaluates once, at the application's first body write — by which point the response head is
  set. Request capture is decided upfront from the request headers.
- **Per-endpoint overrides read after `next`, not before.** The logging middleware sits ahead
  of routing (it must see unrouted requests), so no endpoint is known when it starts. By the
  time the downstream pipeline completes, routing has installed `IRouteMatchFeature`, and one
  metadata lookup (`GetMetadata<HttpLoggingMetadata>`, last-wins) is effectively free. The
  consequence is honest and documented: an override freely widens/narrows *emission-time*
  fields (request line, headers, status, duration — all still readable post-pipeline), but the
  *capture* fields (`RequestBody`/`ResponseBody`/`BytesTransferred`) can only narrow, because
  the streams were armed (or not) before routing ran. `HttpLoggingFields.None` suppresses the
  entry entirely — the health-probe case. `HttpLoggingMetadata` is a sealed concrete carrier
  per the metadata-carrier discipline; there is no `IHttpLoggingMetadata`.
- **Effective client address is a seam, not a guess.** The default logs the transport socket
  peer (`IHttpConnectionInfo.RemoteIp`) — the only honest answer until a trust model exists.
  The middleware never parses `X-Forwarded-For` itself; when the forwarded-headers middleware
  (#778) merges, its trusted result plugs in through
  `HttpLoggingOptions.ClientAddressResolver`. A faulting resolver falls back to the socket peer
  rather than failing the exchange.
- **One package, two halves.** The provider could live in `libraries/Logging.File`, but the W3C
  format is defined by HTTP exchange semantics (`cs-method`, `sc-status`, `time-taken`), i.e. by
  the attribute contract this package owns. Shipping them together keeps the contract and its
  renderer in one place; a general-purpose file logging provider remains future Logging-area
  work (see Non-goals).
- **`Logger`/`LoggerProvider`/`ScopedLogger` base classes, not raw interfaces.** The provider
  subclasses the Logging library's guided bases — the repo's interface-first-with-guided-base
  pattern — inheriting category validation, idempotent disposal, and the single-virtual-call
  hot path.

## Emission model

One entry per completed exchange, emitted in the middleware's `finally`:

- **Level** — `Options.Level` (default `Information`); escalated to `Error` with the exception
  attached when the downstream pipeline throws (the exception is rethrown — observing is this
  package's job, the exception *boundary* is #881's).
- **Message** — `"GET /orders -> 200 in 12.345 ms"`, composed only from enabled fields
  (invariant culture, `string.Create`); `"(faulted)"` appended on exceptions.
- **Attributes** — per the `HttpLoggingAttributes` contract, only for enabled fields.
- **Never throws.** Attribute building is guarded; a logging failure cannot fail an exchange or
  mask an application exception mid-unwind. Sink failures are already isolated by the logging
  pipeline's own contract.
- **Fast off-switch:** when the composed logger reports `Options.Level` disabled, the
  middleware is a pure pass-through — no timestamps, no wrappers, no allocation.

Duration comes from `TimeProvider.GetTimestamp()`/`GetElapsedTime` and entry timestamps from
`TimeProvider.GetUtcNow()`, so tests can substitute a fake `TimeProvider` for deterministic
output.

## The W3C provider

- **Formats.** `W3CExtended` (the W3C Extended Log File Format: `#Version`/`#Fields`
  directives, fixed field list, `-` for absent, spaces `+`-encoded in string fields per the IIS
  convention) plus NCSA `Common` and `Combined`. `cs-bytes`/`sc-bytes` are the body byte counts
  observed at the application layer (headers are not included), and `time-taken` is seconds at
  millisecond precision.
- **Files.** `{prefix}-{yyyyMMdd}.log` per UTC day, rolling to `{prefix}-{yyyyMMdd}.{seq:000}.log`
  when `FileSizeLimit` (approximate, char-counted) is exceeded; a restart appends to the day's
  current file. Retention deletes oldest-first (by last-write time) beyond
  `RetainedFileCountLimit`, never the active file. Rolling keys off the **entry's** timestamp,
  which keeps the writer deterministic under a fake upstream `TimeProvider`.
- **Buffering.** Writes are lock-serialized into a buffered `StreamWriter` and flushed on a
  timer (`FlushInterval`, default 1 s; `TimeSpan.Zero` = flush every write), on `Flush()`, and
  on disposal. Lines from concurrent exchanges never interleave.
- **Log-injection defense.** Rendering strips control characters from every text field and
  escapes quotes/backslashes inside NCSA quoted fields; a hostile `User-Agent` cannot forge log
  lines. Sanitization happens at the text boundary — entry attributes keep the raw values for
  structured consumers.
- **Scoping fan-out.** The provider filters by the `http.event` attribute, so co-registration
  with application logging is safe by default; a `LoggerFilterRule` scoped to
  `typeof(W3CAccessLogProvider)` can additionally trim fan-out on high-volume factories.

## Ordering guidance

`UseHttpLogging` belongs **first** in the pipeline — before authentication, CORS, host
filtering, and routing — so rejected and unrouted exchanges are still logged. Two consequences
to be aware of:

- Anything registered *before* it is invisible to the access log.
- Captured bodies are whatever crosses the wire at its position: place it after a
  decompression middleware to capture decoded payloads, before it to capture the raw ones.

## AOT posture

No reflection anywhere: field selection is a flags enum, per-endpoint discovery is the
`is`-test-based metadata bag, header allowlists are `FrozenSet<HttpHeaderKey>`, and all
numeric/date rendering goes through invariant `TryFormat` into stack buffers. Body capture
decodes UTF-8 into a string only at emission. The package carries the repo-wide
`IsAotCompatible=true` with nothing to suppress.

## Non-goals

- **No general-purpose file logging provider.** The W3C writer renders HTTP exchanges only. A
  text/JSON file provider for application logging is Logging-area work; when it exists, the
  rotation machinery here is the reference implementation to lift.
- **No proxy trust model.** `X-Forwarded-For`/`Forwarded` parsing and trust decisions are #778's
  middleware; this package only exposes the resolver seam.
- **No error handling.** The middleware observes exceptions and rethrows; status-code pages and
  the exception boundary are #881 (over the #864 `OnError` hook).
- **No push/export telemetry.** OTLP export, metrics, and `EventSource` counters are the
  OpenTelemetry epic (#317). The attribute contract is the handoff point.
- **No per-request configuration surface.** Everything is builder-time; the only per-request
  variance is the endpoint metadata override, itself attached at map time.
