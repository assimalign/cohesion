# Event Sources

Cohesion libraries report their runtime diagnostics — lifecycle events, failures, and counters —
through `System.Diagnostics.Tracing.EventSource`. Every event source is **internal** to the library
that raises it: there is no public diagnostics type to reference, call, or mock. What is public is each
source's **name**, and it is always the name of the assembly that raises it. This page is the list of
those names and the two ways to observe them.

The authoring rules are in [`.claude/rules/event-source.md`](../.claude/rules/event-source.md).

## Observing an event source

### From outside the process

The .NET diagnostic tools read event sources by name, with no application code:

```bash
# Events at Verbose (level 5) from one driver.
dotnet-trace collect --process-id <pid> --providers Assimalign.Cohesion.Connections.Tcp::5

# Counters from every Connections driver.
dotnet-counters monitor --process-id <pid> \
    --counters Assimalign.Cohesion.Connections.Tcp,Assimalign.Cohesion.Connections.Quic
```

`dotnet-monitor` and PerfView take the same names.

### Inside the process

`Assimalign.Cohesion.Logging.EventSource` forwards event sources into an `ILoggerFactory`. Each event
becomes one log entry whose category is the event source name — so the logger filter rules you already
write decide both what is logged and how verbosely each source is enabled:

```csharp
using ILoggerFactory loggerFactory = new LoggerFactoryBuilder()
    .AddProvider(new ConsoleLoggerProvider())
    .SetMinimumLevel(LogLevel.Information)
    .AddRule("Assimalign.Cohesion.Connections", LogLevel.Debug)
    .AddEventSourceForwarding()
    .Build();
```

The factory owns the forwarder: forwarding starts when the factory is built and stops when it is
disposed. Web entry-point builders (`CreateBuilder(args)`) forward the `Assimalign.Cohesion.Web.`
prefix and `Assimalign.Cohesion.Http.Connections` by default, unless opted out with
`EnableEventSourceForwarding = false` or an application forwarder is already registered. Nested/options
builders do not. No provider or filter rule is added. To select other sources, register
`builder.Logging.AddEventSourceForwarding(options)` before Build. Listeners observe the whole process,
so concurrent entry points can duplicate events; use payload server IDs for attribution. To forward into a factory you
did not build, `loggerFactory.ForwardEventSources()` returns a forwarder that you dispose yourself.

Calling `AddEventSourceForwarding()` without options selects every source named
`Assimalign.Cohesion.*`; the Web entry-point default selects the narrower names above. A runtime
source is added by prefix (`new EventSourceForwardingOptions { Sources = { "System.Net.Security" } }`). The package's
[DESIGN.md](../libraries/Logging/Assimalign.Cohesion.Logging.EventSource/docs/DESIGN.md) has the level
mapping, the entry shape, and the failure model.

### NativeAOT

Sdk.Web enables EventSource support for NativeAOT when unset; an explicit false wins. Applications
using other SDKs must opt in:

```xml
<PropertyGroup>
    <EventSourceSupport>true</EventSourceSupport>
</PropertyGroup>
```

Without it, no tool and no forwarder receives any event; the libraries behave identically either way.
No Cohesion SDK changes the .NET SDK default. The Database project templates set the property in the
generated csproj, so a new database executable keeps its engine's events and can delete the line to
opt out (owner decision, 2026-10-09).

## Cohesion event sources

The Web catalog contains 22 sources, including `Web.Authentication.Certificate`, with one row per
instrumented assembly. The [Web signal ownership table](resources/Web/DESIGN.md#diagnostic-signal-ownership)
explains when to use an event, a request span, a metric, or the HTTP access log.

| Event source (= assembly) | Events | Counters | Reference |
| --- | --- | --- | --- |
| `Assimalign.Cohesion.ApplicationModel.Gateway` | Run starting (epoch), started, stopped; run superseded by a later run (Warning); run failed (Error); per resource: planned, deploying, ready, retired; missed its window (Error), workload stopped before it published (Error), skipped because a dependency did not start (Warning); an operator's action run on an instance, its write recorded, an instance unregistered because the gateway could not call it (Warning) | None | [Gateway DESIGN.md](../libraries/ApplicationModel/Assimalign.Cohesion.ApplicationModel.Gateway/docs/DESIGN.md#diagnostics) |
| `Assimalign.Cohesion.Connections` | Upgrade failed: a layered listener (a TLS listener, for example) closed a connection whose upgrade failed or timed out, and kept accepting | `current-upgrades`, `failed-upgrades` | [Connections DESIGN.md](../libraries/Connections/Assimalign.Cohesion.Connections/docs/DESIGN.md#the-layered-listeners-event-source) |
| `Assimalign.Cohesion.Connections.Tcp` | Listener bound/closed; queued connection skipped because its client reset it, or on Linux a network error was pending on it, before the accept (Verbose); accept backed off for want of descriptors or buffers, or on Unix for an error .NET does not name (Warning, at most once a second per listener); accepted connection closed because its socket could not be set up (Verbose); connection opened/closed; peer end-of-stream, back-pressure pause/resume, reset (Verbose); connection error | `current-connections`, `total-connections`, `connections-per-second` | [Tcp DESIGN.md](../libraries/Connections/Assimalign.Cohesion.Connections.Tcp/docs/DESIGN.md#diagnostics) |
| `Assimalign.Cohesion.Connections.Quic` | Listener bound/closed; connection opened/closed; stream opened/closed (Verbose); inbound handshake failed and dropped | `current-connections`, `total-connections`, `connections-per-second`, `current-streams`, `streams-per-second` | [Quic DESIGN.md](../libraries/Connections/Assimalign.Cohesion.Connections.Quic/docs/DESIGN.md#diagnostics) |
| `Assimalign.Cohesion.Connections.NamedPipes` | Listener bound/closed; connection opened/closed | `current-connections`, `total-connections`, `connections-per-second` | [NamedPipes DESIGN.md](../libraries/Connections/Assimalign.Cohesion.Connections.NamedPipes/docs/DESIGN.md#diagnostics) |
| `Assimalign.Cohesion.Connections.Udp` | Datagram connection opened (bind or connect)/closed | `current-connections`, `total-connections` | [Udp DESIGN.md](../libraries/Connections/Assimalign.Cohesion.Connections.Udp/docs/DESIGN.md#diagnostics) |
| `Assimalign.Cohesion.Database` | Engine created, composed, dispose start/stop; engine dispose failed (Error); engine worker failed (per database, or a whole pass; Warning), recovered, loop faulted (Error), give-up failed (Error), pass start/stop and unfinished work (Verbose, `Workers`); database created, opened, dropped, closed; database operation failed (Error); database taken offline after a worker's failures persisted (Error); server started, stopped; server start failed (Error); server session negotiated (Verbose, `Sessions`), authenticated; session opened/closed (Verbose, `Sessions`); statement start/stop (Verbose, `Statements`); slow statement (Warning; `SlowStatementThresholdMs` argument, default 1000); statement failed (Error, by code and type); explicit transaction begun, committed, rolled back, aborted (Verbose, `Transactions`); commit failed (Error, by type); every stop carries its status | `current-sessions` | [Database DESIGN.md](../resources/Database/Assimalign.Cohesion.Database/docs/DESIGN.md#diagnostics) |
| `Assimalign.Cohesion.Database.Blob` | Server session accepted, closed (Verbose); connection rejected, handshake refused or timed out, protocol violation, session cleanup failed, sessions aborted at shutdown (Warning); session faulted (Error); database refused for a failing worker, engine refused, host transaction abort failed (Warning); transfer failed (Error, by code and type) | `current-server-sessions`, `total-server-sessions`, `total-rejected-sessions` | [Database.Blob DESIGN.md](../resources/Database/Assimalign.Cohesion.Database.Blob/docs/DESIGN.md#diagnostics) |
| `Assimalign.Cohesion.Database.Blob.Client` | Transfer start/stop per upload, download, delete, properties and listing (Verbose, `Transfers`); transfer failed (Error, by code and type), then its stop with `Error`; every start has a stop with its status (`Success`, `Error`, `Cancelled`); listing cleanup failure the consumer never saw (Warning) | none | [Database.Blob.Client DESIGN.md](../resources/Database/Assimalign.Cohesion.Database.Blob.Client/docs/DESIGN.md#diagnostics) |
| `Assimalign.Cohesion.Database.Client` | Connection opened, closed; failed to open (Error); broken (Warning); rented, returned (Verbose, `Pool`); exchange failed with a coded error (Verbose, by code and type); failed download's rental not returned (Warning) | `current-connections`, `current-rented-connections`, `connections-opened-per-second`, `total-connection-failures` | [Database.Client DESIGN.md](../resources/Database/Assimalign.Cohesion.Database.Client/docs/DESIGN.md#diagnostics) |
| `Assimalign.Cohesion.Database.Documents` | Index recovery start/stop; wire session lifecycle (Verbose, `Sessions`), throttled Warning/Error failures with suppressed counts | `current-server-sessions`, `total-server-sessions`, `total-rejected-sessions` | [Database.Documents DESIGN.md](../resources/Database/Assimalign.Cohesion.Database.Documents/docs/DESIGN.md#diagnostics) |
| `Assimalign.Cohesion.Database.Graph` | Server session accepted, closed (Verbose); connection rejected, handshake refused or timed out, protocol violation, session cleanup failed, sessions aborted at shutdown (Warning); session faulted (Error); index recovery start/stop when a database reopens, the stop with its status; wire statement failed to parse (Error, by code and type) | `current-server-sessions`, `total-server-sessions`, `total-rejected-sessions` | [Database.Graph DESIGN.md](../resources/Database/Assimalign.Cohesion.Database.Graph/docs/DESIGN.md#diagnostics) |
| `Assimalign.Cohesion.Database.Documents.Client` | Query/command start and stop (Verbose, `Queries`); failures (Error, code and type), throttled once per second with suppressed count; no statement or document contents | none | [Documents.Client DESIGN.md](../resources/Database/Assimalign.Cohesion.Database.Documents.Client/docs/DESIGN.md#diagnostics) |
| `Assimalign.Cohesion.Database.Graph.Client` | Query start/stop per query, execute and path query (Verbose, `Queries`), every start closed by a stop with its status (`Success`, `Error`, `Cancelled`); query failed (Error, by code and type) | none | [Database.Graph.Client DESIGN.md](../resources/Database/Assimalign.Cohesion.Database.Graph.Client/docs/DESIGN.md#diagnostics) |
| `Assimalign.Cohesion.Database.Hosting` | Offline database found (Warning); reopen attempted, succeeded; reopen failed (Warning); reopen abandoned; reopen loop failed (Error); application built, starting, started, stopped; accepted application start failed after rollback (Error, ID 10; excludes caller cancellation and lifecycle rejection; one-second reporting budget); disposal failed (Error); resource command handled (Informational) or rejected (Warning); admin endpoint failed (Error); observed database health degraded (Warning, ID 16) and restored (Informational, ID 17), once per condition transition | none | [Database.Hosting DESIGN.md](../resources/Database/Assimalign.Cohesion.Database.Hosting/docs/DESIGN.md#diagnostics) |
| `Assimalign.Cohesion.Database.Indexing` | Index created/dropped (Verbose); writers purged at recovery; page split and root growth (Verbose, `Splits` keyword); format refusal, page corruption, split invariant violated (Error) | none | [Database.Indexing DESIGN.md](../resources/Database/Assimalign.Cohesion.Database.Indexing/docs/DESIGN.md#diagnostics) |
| `Assimalign.Cohesion.Database.KeyValuePair` | Server session accepted, closed (Verbose); connection rejected, handshake refused or timed out, protocol violation, session cleanup failed, sessions aborted at shutdown (Warning); session faulted and command parse failed (Error; parse failures throttled to one per second with `suppressedCount`) | `current-server-sessions`, `total-server-sessions`, `total-rejected-sessions`, `total-statement-parse-failures` | [Database.KeyValuePair DESIGN.md](../resources/Database/Assimalign.Cohesion.Database.KeyValuePair/docs/DESIGN.md#diagnostics) |
| `Assimalign.Cohesion.Database.KeyValuePair.Client` | Command start/stop (Verbose, `Commands`), every start closed by a stop with its status (`Success`, `Error`, `Cancelled`); command failed (Error, by code and type); observer hook failed (Warning); the SQL client's ids and payloads | none | [Database.KeyValuePair.Client DESIGN.md](../resources/Database/Assimalign.Cohesion.Database.KeyValuePair.Client/docs/DESIGN.md#diagnostics) |
| `Assimalign.Cohesion.Database.Protocol` | Frame read, frame written, once per wire frame (Verbose, `Frames`) | none | [Database.Protocol DESIGN.md](../resources/Database/Assimalign.Cohesion.Database.Protocol/docs/DESIGN.md#diagnostics) |
| `Assimalign.Cohesion.Database.Security` | Authentication succeeded, rejected (Verbose); authenticator failed (Error) | none | [Database.Security DESIGN.md](../resources/Database/Assimalign.Cohesion.Database.Security/docs/DESIGN.md#diagnostics) |
| `Assimalign.Cohesion.Database.Sql` | Server session accepted, closed (Verbose); connection rejected, handshake refused or timed out, protocol violation, session cleanup failed, sessions aborted at shutdown (Warning); session faulted (Error); statement access path and records examined, DDL self-commit (Verbose, `Statements`); statement too complex (Warning); typed scalar, aggregate, type and cast registration, including built-ins (Verbose); provisioning Start/Stop and coded failure (IDs 13-15); deferred step bound/completed, throttled progress (Verbose), and coded failure (IDs 18-21); ID 17 retired; declared script applied (Informational, ID 22: engine, database, name, SHA-256; never body); table reclamation started/completed (Informational, IDs 23/24: engine, database, retired object id; completed adds pages reclaimed in this process). Pending space alone does not degrade health | `current-server-sessions`, `total-server-sessions`, `total-rejected-sessions` | [Database.Sql DESIGN.md](../resources/Database/Assimalign.Cohesion.Database.Sql/docs/DESIGN.md#diagnostics) |
| `Assimalign.Cohesion.Database.Sql.Client` | Command start/stop (Verbose, `Commands`), every start closed by a stop with its status (`Success`, `Error`, `Cancelled`); command failed (Error, by code and type); observer hook failed (Warning) | none | [Database.Sql.Client DESIGN.md](../resources/Database/Assimalign.Cohesion.Database.Sql.Client/docs/DESIGN.md#diagnostics) |
| `Assimalign.Cohesion.Database.Storage` | Storage created; recovery start/stop, the stop with its status; journal tail lost (Warning); checkpoint start/stop (`Checkpoints` keyword), the stop with its status; paced write-back (Verbose, `WriteBack`), group flush and missed group-commit window (Verbose, `GroupCommit`), dirty page evicted (Verbose, `BufferPool`); storage offline, commit unconfirmed, buffer pool exhausted, page checksum failed (Error); shutdown flush skipped (Warning); buffer pool resized; storage closed | `current-storages`, `checkpoints-per-second`, `journal-flushes-per-second`, `page-reads-per-second`, `page-writes-per-second`, `foreground-page-writes-per-second`, `group-commit-self-flushes-per-second`, `pre-images-spilled-per-second` | [Database.Storage DESIGN.md](../resources/Database/Assimalign.Cohesion.Database.Storage/docs/DESIGN.md#diagnostics) |
| `Assimalign.Cohesion.Database.Transactions` | Kernel transaction begun/committed/rolled back (Verbose, `Transactions` keyword); lock wait start/stop (Verbose, `Locks` keyword); deadlock, slow lock wait (`SlowLockWaitThresholdMs` argument, 1000 ms default), lock wait still blocked at that threshold with its blockers, lock waits abandoned, commit record write failed, undo deferred, abort record lost, deferred checkpoint failed (Warning); commit unconfirmed (Error); deferred undo completed, recovery analyzed; deferred checkpoints and version-purge passes (Verbose, `Checkpoints` and `Purge` keywords) | `current-transactions`, `transactions-per-second`, `commits-per-second`, `rollbacks-per-second`, `total-deadlocks`, `lock-waits-per-second` | [Database.Transactions DESIGN.md](../resources/Database/Assimalign.Cohesion.Database.Transactions/docs/DESIGN.md#diagnostics) |
| `Assimalign.Cohesion.DependencyInjection` | Provider built; call site built, service resolved, scope disposed, provider descriptors, resolver compiled (Verbose); resolver compilation failed | none | [DependencyInjection DESIGN.md](../libraries/DependencyInjection/Assimalign.Cohesion.DependencyInjection/docs/DESIGN.md#diagnostics) |
| `Assimalign.Cohesion.Http.Connections` | Transport rejections, HTTP/2 and HTTP/3 connection errors and stream resets, slow-reader aborts and client-caused response aborts (Warning, throttled per event with `suppressedCount`) | `rejected-requests`, `connection-errors`, `stream-resets`, `response-rate-aborts`, `response-aborts` | [Http.Connections DESIGN.md](../libraries/Http/Assimalign.Cohesion.Http.Connections/docs/DESIGN.md#diagnostics) |
| `Assimalign.Cohesion.Http.Sessions` | Store load/commit failures (type only, throttled) | `store-load-failures`, `store-commit-failures` | [Design](../libraries/Http/Assimalign.Cohesion.Http.Sessions/docs/DESIGN.md#diagnostics) |
| `Assimalign.Cohesion.Web.Antiforgery` | RequestRejected and sanitized TokenUnprotectFailed (Verbose); RejectionAborted (Warning, at most once a second with suppressedCount); FormMalformed has no exception text | `rejected-requests` | [Antiforgery DESIGN.md](../resources/Web/Assimalign.Cohesion.Web.Antiforgery/docs/DESIGN.md#diagnostics) |
| `Assimalign.Cohesion.Web.Authentication.Bearer` | TokenRejected (Verbose): bounded rejection reason, algorithm/key id and request correlation; no token or claims | `rejected-tokens` | [Bearer DESIGN.md](../resources/Web/Assimalign.Cohesion.Web.Authentication.Bearer/docs/DESIGN.md#diagnostics) |
| `Assimalign.Cohesion.Web.Authentication.Certificate` | CertificateRejected (Verbose): NoCertificate on a required challenge or ValidationFailed from application mapping; no certificate identifiers. TLS trust/validity failures occur before HTTP and are unavailable to this handler | `rejected-certificates` | [Certificate DESIGN.md](../resources/Web/Assimalign.Cohesion.Web.Authentication.Certificate/docs/DESIGN.md#diagnostics) |
| `Assimalign.Cohesion.Web.Authentication.Cookie` | TicketRejected (Verbose): bounded reason and sanitized unprotect exception metadata with request correlation | `rejected-tickets` | [Cookie DESIGN.md](../resources/Web/Assimalign.Cohesion.Web.Authentication.Cookie/docs/DESIGN.md#diagnostics) |
| `Assimalign.Cohesion.Web.Authorization` | RequestDenied (Verbose): challenge/forbid outcome, route template, policy source, failed requirement type, schemes and request correlation | `challenged-requests`, `forbidden-requests` | [Authorization DESIGN.md](../resources/Web/Assimalign.Cohesion.Web.Authorization/docs/DESIGN.md#diagnostics) |
| `Assimalign.Cohesion.Web.Caching` | ResponseNotStored (Verbose); EntryDeclined (Warning, throttled); no cache keys or tags | `declined-entries`; hit/miss counters omitted after benchmarking | [Caching DESIGN.md](../resources/Web/Assimalign.Cohesion.Web.Caching/docs/DESIGN.md#diagnostics) |
| `Assimalign.Cohesion.Web.Compression` | Request decompression refused (Verbose); decompressed size exceeded or rejection aborted after response commit (Warning, throttled) | `decompression-limit-rejections` | [Compression DESIGN.md](../resources/Web/Assimalign.Cohesion.Web.Compression/docs/DESIGN.md#diagnostics) |
| `Assimalign.Cohesion.Web.Cors` | Refused preflights and pass-through causes, with bounded origin and rejected header name (Verbose) | `rejected-preflights` | [Cors DESIGN.md](../resources/Web/Assimalign.Cohesion.Web.Cors/docs/DESIGN.md#diagnostics) |
| `Assimalign.Cohesion.Web.Diagnostics` | Access-log file opened; write/flush/close failed (Error), retention failed, HTTP log entry dropped, client-address resolver failed (Warning); failures throttled | `failed-access-log-writes`, `dropped-http-log-entries` | [Diagnostics DESIGN.md](../resources/Web/Assimalign.Cohesion.Web.Diagnostics/docs/DESIGN.md#diagnostics) |
| `Assimalign.Cohesion.Web.ErrorHandling` | Application pipeline fault (Error), expected fault (Verbose), error-handler failure (Error), diagnostic-callback failure (Warning), peer-abandoned response (Verbose); client-caused and cancelled exchanges excluded from application-fault reporting | `total-faults`, `faults-per-second`, `aborted-responses` | [ErrorHandling DESIGN.md](../resources/Web/Assimalign.Cohesion.Web.ErrorHandling/docs/DESIGN.md#diagnostics) |
| `Assimalign.Cohesion.Web.Forms` | Form limit or malformed input (Verbose); rejection after response commit (Warning, throttled); configured limit names and exception types only | `form-limit-rejections`, `malformed-forms` | [Forms DESIGN.md](../resources/Web/Assimalign.Cohesion.Web.Forms/docs/DESIGN.md#diagnostics) |
| `Assimalign.Cohesion.Web.ForwardedHeaders` | Forwarding stop reason, family and applied depth (Verbose); transport peer IP only for an untrusted peer at depth zero | `untrusted-forwarding`, `malformed-forwarding` | [ForwardedHeaders DESIGN.md](../resources/Web/Assimalign.Cohesion.Web.ForwardedHeaders/docs/DESIGN.md#diagnostics) |
| `Assimalign.Cohesion.Web.Health` | Health worsened (Warning), improved (Informational), thrown or timed-out check (Verbose); includes adapted Hosting.Health contributors | `failed-checks` | [Health DESIGN.md](../resources/Web/Assimalign.Cohesion.Web.Health/docs/DESIGN.md#diagnostics) |
| `Assimalign.Cohesion.Web.HostFiltering` | Rejected effective host with bounded host component, reason and request id (Verbose) | `rejected-requests` | [HostFiltering DESIGN.md](../resources/Web/Assimalign.Cohesion.Web.HostFiltering/docs/DESIGN.md#diagnostics) |
| `Assimalign.Cohesion.Web.Server` | Server lifecycle and failures, peer-ended connections (Verbose), refused response heads and telemetry listener failures (throttled), completion callback and isolation failures; process-unique serverId | `connection-faults`, `connection-cap-waits`, `refused-response-heads` | [Server DESIGN.md](../resources/Web/Assimalign.Cohesion.Web.Server/docs/DESIGN.md#diagnostics) |
| `Assimalign.Cohesion.Web.RateLimiting` | Rejected global or endpoint policy (Verbose); rejection after response commit (Warning, throttled); partition keys are never written | `rejected-requests` | [RateLimiting DESIGN.md](../resources/Web/Assimalign.Cohesion.Web.RateLimiting/docs/DESIGN.md#diagnostics) |
| `Assimalign.Cohesion.Web.RequestTimeouts` | Timeout enforced (Warning, throttled) or ignored by downstream work (Verbose); armed interval and request correlation | `timed-out-requests`, `ignored-timeouts` | [RequestTimeouts DESIGN.md](../resources/Web/Assimalign.Cohesion.Web.RequestTimeouts/docs/DESIGN.md#diagnostics) |
| `Assimalign.Cohesion.Web.SecurityHeaders` | Security policy staging skipped by read-only headers (Warning, throttled); excludes disabled endpoints and protocol takeovers | `unprotected-responses` | [SecurityHeaders DESIGN.md](../resources/Web/Assimalign.Cohesion.Web.SecurityHeaders/docs/DESIGN.md#diagnostics) |
| `Assimalign.Cohesion.Web.Sessions` | SessionOrphaned (Verbose), SessionStateLost (modified orphans only, Warning, throttled); store failures belong to Http.Sessions | `orphaned-sessions`, `lost-sessions` | [Sessions DESIGN.md](../resources/Web/Assimalign.Cohesion.Web.Sessions/docs/DESIGN.md#diagnostics) |
| `Assimalign.Cohesion.Web.StaticFiles` | WebRootMounted; WebRootUnavailable; TraversalRejected (Verbose); MountLookupFailed (Warning, type only, throttled) | `traversal-rejections`, `mount-lookup-failures` | [StaticFiles DESIGN.md](../resources/Web/Assimalign.Cohesion.Web.StaticFiles/docs/DESIGN.md#diagnostics) |
| `Assimalign.Cohesion.Web.WebSockets` | HandshakeRejected, WebSocketOpened, WebSocketClosed, DrainCloseFailed (all Verbose) | `current-websockets`, `total-websockets`, `rejected-handshakes` | [WebSockets DESIGN.md](../resources/Web/Assimalign.Cohesion.Web.WebSockets/docs/DESIGN.md#diagnostics) |

Deliberately not instrumented: `Connections.InMemory` (a test driver) and `Connections.Security` (TLS
handshakes are already reported by the runtime's `System.Net.Security` source, and a handshake that
fails on a TLS-layered listener is reported by `Assimalign.Cohesion.Connections`, which owns the
listener that closes the connection).

HTTP transport rejections and protocol errors belong to `Assimalign.Cohesion.Http.Connections`
(#1300), including requests rejected before Web dispatch and therefore absent from a Web request
span. Its Meter and EventSource complement Web.Server's request signals; connection lifecycle
and connection counts remain with the Connections drivers.

### Web assemblies deliberately left uninstrumented

These 17 Web assemblies deliberately have no event source, alongside the framework producer
shells. Each row records the reason from
[Web event-source scope §6](programs/WEB_EVENT_SOURCES_SCOPE.md#6-no-source-and-why).
`Web.Authentication.Certificate` is instrumented and appears in the catalog above.

| Assembly or framework project | Why it has no source |
| --- | --- |
| `Assimalign.Cohesion.Web` | Contracts and pure pipeline helpers have no runtime state, loop or swallowed failure; the root must not claim every Web package's signals. |
| `Assimalign.Cohesion.Web.Testing` | A shipped test harness, like Connections.InMemory, rather than a runtime component; it receives no access to internal sources. |
| `Assimalign.Cohesion.Web.Hosting` | Its control-plane events left with the retired resource integration; the server, connection and request signals belong to Web.Server. |
| `Assimalign.Cohesion.Web.Hosting.ApplicationModel` | The resource integration is being rebuilt; its control-plane outcomes are HTTP statuses the gateway sees, and diagnostics come with the design. |
| `Assimalign.Cohesion.Web.ApplicationModel` | Invalid declarative plans throw; runtime control-plane outcomes belong to ApplicationModel.Hosting. |
| `Assimalign.Cohesion.Web.Routing` | Failures throw at startup or reach the exception boundary; 404/405 are statuses and the matched template is already the span's `http.route`. |
| `Assimalign.Cohesion.Web.Api` | Binding failures are visible client-driven 400s in access logs and request metrics; the signal does not justify a new public rejection API. |
| `Assimalign.Cohesion.Web.OpenApi` | Build failures reach the boundary and are not cached; first-route-wins is documented and the package is development-facing. |
| `Assimalign.Cohesion.Web.Validation` | A 400 is the intended client outcome, and validation errors echo user input. |
| `Assimalign.Cohesion.Web.Serialization` | A registry without lifecycle: faults throw and failed negotiation returns false for the caller's 406. |
| `Assimalign.Cohesion.Web.Query` | Outcomes are visible client-driven protocol statuses (400, 415, 406, 304, 412), with nothing swallowed. |
| `Assimalign.Cohesion.Web.ProblemDetails` | A payload library without lifecycle; deterministic degradation serves last-chance error handling. |
| `Assimalign.Cohesion.Web.Authentication` | Handlers own failure decisions and their sources; dispatcher counts would be inexact because Authorization re-enters per scheme. |
| `Assimalign.Cohesion.Web.Rewrite` | Loops and regex timeouts throw to the boundary, while redirects are configured answers. |
| `Assimalign.Cohesion.Web.HttpsPolicy` | Redirect rates are visible by route in request metrics; a source cannot reliably diagnose loops caused by absent forwarded-header middleware. |
| `Assimalign.Cohesion.Web.CookiePolicy` | Applications observe drops through `OnRejected`; consent drops and default `Upgrade` repairs are intended policy outcomes. |
| `Assimalign.Cohesion.Web.Refs` / `Assimalign.Cohesion.Web.Runtime` | Framework producer shells that compile nothing. |

## Traces and metrics

Where a library emits OpenTelemetry traces and metrics, it does so through the BCL's
`System.Diagnostics.ActivitySource` and `System.Diagnostics.Metrics.Meter`, and the naming rule is
the same: both are named for the assembly that emits them, so one name enables both. Subscribe in
process with an `ActivityListener` and a `MeterListener` (an OpenTelemetry SDK registers the same
names); out of process, `dotnet-counters` reads a meter by name. The forwarder above handles event
sources only.

| Assembly (same-named `ActivitySource` and/or `Meter`) | Traces | Metrics | Reference |
| --- | --- | --- | --- |
| `Assimalign.Cohesion.Web.Server` | One `Server` span per HTTP request, parented to the caller's `traceparent` | `http.server.request.duration`, `http.server.active_requests` | [Request telemetry](../resources/Web/Assimalign.Cohesion.Web.Server/docs/DESIGN.md#server-telemetry-1064); [same-named EventSource for server diagnostics](../resources/Web/Assimalign.Cohesion.Web.Server/docs/DESIGN.md#diagnostics) |
| `Assimalign.Cohesion.Http.Connections` | None | `cohesion.http.server.rejected_requests`, `cohesion.http.server.protocol_errors`, `cohesion.http.server.response_rate_aborts`, `cohesion.http.server.response_aborts` | [Http.Connections DESIGN.md](../libraries/Http/Assimalign.Cohesion.Http.Connections/docs/DESIGN.md#meter-instruments) |

Web event sources report operator events and exact attribution counters, rather than duplicating
request counts or spans with per-request start/stop events. The [Web signal ownership table](resources/Web/DESIGN.md#diagnostic-signal-ownership)
also distinguishes transport rejections from requests that reached Web middleware. EventSource
forwarding carries events into configured sinks; it does not forward counters or Meter instruments.

In the Database area (the per-project decisions are in
[`docs/programs/DATABASE_EVENT_SOURCES_PLAN.md`](programs/DATABASE_EVENT_SOURCES_PLAN.md), §3):
`Database.Types` (value types and codecs: pure functions whose failures are exceptions to the
caller); `Database.Language`, `Database.Sql.Language`, `Database.Graph.Language` and
`Database.Documents.Language` (parsers with no I/O and no lifecycle; a parse error reaches the
session, which the root reports as a failed statement); `Database.Execution` (request and result
contracts); the five `<Model>.Catalog` and five `<Model>.Storage` packages (catalog and record-format
adapters over `Database.Storage`, which reports their I/O; a DDL refusal is a failed statement, and
the catalogs' swallowed reads skip reclaimed MVCC versions, an expected outcome); `Database.Sql.Tcp`
(one extension method configuring a listener `Connections.Tcp` reports); `Database.Embedded` (holds
and disposes engines, whose lifecycle the root reports); `Database.Testing` (a test harness);
`Database.ApplicationModel` (a declarative planner a gateway runs at build, with no run-time work);
and `Sdk.Database.Tasks` (MSBuild tasks log through `TaskLoggingHelper` into the build log, inside a
node no application tool attaches to). `Database.Sql.Schema` also remains uninstrumented after
the redesign: compilation, validation and migration planning transform in-memory values during
application composition as well as in MSBuild. `SqlCompiledSchemaSerializer.Read`/`ReadAsync`
and `Write`/`WriteAsync` also read and write files and create write directories. Their I/O and
cancellation failures propagate unchanged, and malformed JSON retains its original inner
exception when translated to a validation failure. The caller owns reporting; these helpers
have no service or background lifecycle and swallow no operational failure. Database.Sql owns
schema application, so no Sql.Schema source is added.

## Not yet conforming

These sources predate the convention. Their names will change when they are brought into line, so do
not build tooling against them.

| Assembly | Current name | Problem | Tracked by |
| --- | --- | --- | --- |
| `Assimalign.Cohesion.Resilience` | `AssimalignCohesionResilience` | Concatenated name; public constructor; no events. | #1038 |
| `Assimalign.Cohesion.Resilience.Retry` | *(empty)* | Empty name; an event method that writes nothing. | #1038 |
| `Assimalign.Cohesion.Resilience.Timeout` | `Assimalign.Cohesion.Resilience.TimeoutResilienceEventSource` | Type name in the source name; no events. | #1038 |
