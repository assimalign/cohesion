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

The package does not read the server's request id (`IWebRequestIdFeature`), and so takes no
`Web.Server` reference. The request id is the same W3C trace id the header carries when it is valid,
but the default server also invents one for a request without a valid `traceparent`. Logging that id
would give every entry a `trace.id`, including entries no span or caller shares. That is a separate
decision: if it is taken, the middleware reads the feature from `Web.Server`, where it moved from
the Web root with #1379, and handles its absence under a custom server.

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
  of routing (it must also log the exchanges middleware ahead of routing reject), so no endpoint
  is known when it starts. `UseRouting` publishes the matched endpoint (`IRouteMatchFeature`) on
  the exchange and calls `next`; the pipeline's terminal runs it (#1054), and the match is still
  there when the pipeline unwinds, so one metadata lookup (`GetMetadata<HttpLoggingMetadata>`,
  last-wins) is effectively free. Before #1054 routing was terminal and published the same
  feature before running the handler, so the post-`next` read carried across the split
  unchanged; only the preflight rule below is new. The
  consequence is honest and documented: an override freely widens/narrows *emission-time*
  fields (request line, headers, status, duration — all still readable post-pipeline), but the
  *capture* fields (`RequestBody`/`ResponseBody`/`BytesTransferred`) can only narrow, because
  the streams were armed (or not) before routing ran. `HttpLoggingFields.None` suppresses the
  entry entirely — the health-probe case. `HttpLoggingMetadata` is a sealed concrete carrier
  per the metadata-carrier discipline; there is no `IHttpLoggingMetadata`. Access logging is
  optional behavior, so the carrier does not name a required middleware
  (`IRouteMiddlewareMetadata`): an endpoint dispatched without `UseHttpLogging` is just not
  logged. Applications attach it with the convention verb `WithHttpLogging(fields)` (#1055), a
  generic extension member over routing's `IRouterConventionBuilder` that serves routes and groups
  alike; routing composes the metadata at route-table build, outer group first.
- **A CORS preflight is logged with the configured fields.** Routing publishes the candidate
  endpoint of a CORS preflight (`IsPreflight`) so CORS can read its metadata, but the candidate
  never runs for the preflight. An override describes the exchanges its endpoint handles, so it
  is not applied to the preflight: otherwise an `OPTIONS` request naming a silenced endpoint would
  vanish from the access log while being answered by something else entirely.
- **The effective identity is read, never guessed.** Scheme, host, and client address are the
  effective values from `Assimalign.Cohesion.Http.Forwarded` (see "Behind a proxy" below): what
  the forwarded-headers trust model vouched for, otherwise the transport's. The middleware never
  parses `Forwarded`/`X-Forwarded-For` itself. `HttpLoggingOptions.ClientAddressResolver`
  remains as an override for a client source that trust model does not cover; a faulting
  resolver falls back to the effective client rather than failing the exchange.
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

## Behind a proxy — effective identity, peer kept beside it

An access log that records the proxy as every request's client is useless for audit, and one that
believes `X-Forwarded-For` from anyone is forgeable (#1050, defect D7). The package reads the
`Effective*` convention of `Assimalign.Cohesion.Http.Forwarded` — owner decision 3 in
`docs/programs/HTTP_WEB_PROGRAM_PLAN.md` §7.4: consumers read the effective values; nothing
rewrites the request — and leaves every trust decision to `Web.ForwardedHeaders`:

| Attribute | Value |
| --- | --- |
| `http.request.scheme` / `http.request.host` | `EffectiveScheme` / `EffectiveHost`: forwarded by a trusted proxy, otherwise the wire values |
| `http.client.address` | `EffectiveRemoteIp` (or the `ClientAddressResolver` result): the client a trusted chain vouched for, otherwise the transport peer |
| `http.client.port` | the effective endpoint's port: the forwarded node's port when a hop resolved the client (omitted when it carried none), otherwise the transport's |
| `network.peer.address` / `network.peer.port` | the transport peer — normally the nearest proxy — emitted only when it is not the logged client |

- **Why both addresses.** Replacing the peer with the forwarded client loses the hop that
  delivered the exchange, which is what a forensic reader needs to spot a misconfigured trust list
  or an unexpected ingress path. The peer pair costs nothing on a direct connection (it is omitted
  when it equals the client) and uses the OpenTelemetry names for exactly this distinction
  (`client.address` behind intermediaries versus `network.peer.address`). The existing attribute
  names are unchanged; only their values became proxy-aware.
- **Why only the effective scheme and host.** Behind a proxy the wire scheme and host describe the
  internal proxy-to-app hop, which is deployment configuration rather than per-exchange evidence,
  so no attribute is spent on them. `IHttpForwardedFeature.OriginalScheme`/`OriginalHost` keep
  them available to any consumer that wants them.
- **Timing, and why `UseHttpLogging` can still run first.** The attributes are read in the
  middleware's `finally`, after the pipeline unwinds; the forwarded-headers middleware leaves its
  feature on the exchange, so the entry carries the forwarded identity even though logging is
  registered ahead of `UseForwardedHeaders`. An exchange rejected *before* `UseForwardedHeaders`
  runs is logged with the transport values.
- **W3C output.** `c-ip` renders `http.client.address` and `cs-host` renders `http.request.host`,
  so access-log files show the forwarded client and host with no format change; the fixed
  `#Fields` list gains no peer column.
- **No trust, no change.** Without the forwarded-headers middleware (or from a peer outside its
  trust model) every effective value is the transport's, so a client that sends
  `X-Forwarded-For` itself cannot forge its logged address.

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
filtering, and routing — so rejected and unrouted exchanges are still logged. Behind a proxy,
`UseForwardedHeaders` follows it, after `UseSecurityHeaders` when that is registered (the area's
[middleware order](../../../../docs/resources/Web/MIDDLEWARE_ORDER.md)): the entry reads the effective identity after the
pipeline unwinds (see "Behind a proxy"). Per-endpoint overrides are read at the same point, so
they need no position relative to `UseRouting`. Two consequences to be aware of:

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
- **No proxy trust model.** `X-Forwarded-For`/`Forwarded` parsing and trust decisions belong to
  `Web.ForwardedHeaders`; this package reads its published result through the `Effective*`
  convention and never re-parses forwarding headers.
- **No error handling.** The middleware observes exceptions and rethrows; status-code pages and
  the exception boundary are #881 (over the #864 `OnError` hook).
- **No push/export telemetry.** OTLP export, metrics, and `EventSource` counters are the
  OpenTelemetry epic (#317). The attribute contract is the handoff point.
- **No per-request configuration surface.** Everything is builder-time; the only per-request
  variance is the endpoint metadata override, itself attached at map time.
