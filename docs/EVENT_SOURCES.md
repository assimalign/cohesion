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
disposed. In a host, call `builder.Logging.AddEventSourceForwarding()`; the factory is built before the
host starts, so the events raised while listeners bind are forwarded too. To forward into a factory you
did not build, `loggerFactory.ForwardEventSources()` returns a forwarder that you dispose yourself.

By default every source named `Assimalign.Cohesion.*` is forwarded; a runtime source is added by
prefix (`new EventSourceForwardingOptions { Sources = { "System.Net.Security" } }`). The package's
[DESIGN.md](../libraries/Logging/Assimalign.Cohesion.Logging.EventSource/docs/DESIGN.md) has the level
mapping, the entry shape, and the failure model.

### NativeAOT

A NativeAOT application compiles EventSource out unless it opts back in:

```xml
<PropertyGroup>
    <EventSourceSupport>true</EventSourceSupport>
</PropertyGroup>
```

Without it, no tool and no forwarder receives any event; the libraries behave identically either way.

## Cohesion event sources

| Event source (= assembly) | Events | Counters | Reference |
| --- | --- | --- | --- |
| `Assimalign.Cohesion.Connections` | Upgrade failed: a layered listener (a TLS listener, for example) closed a connection whose upgrade failed or timed out, and kept accepting | `current-upgrades`, `failed-upgrades` | [Connections DESIGN.md](../libraries/Connections/Assimalign.Cohesion.Connections/docs/DESIGN.md#the-layered-listeners-event-source) |
| `Assimalign.Cohesion.Http.Sessions` | Store load/commit failures (type only, throttled) | `store-load-failures`, `store-commit-failures` | [Design](../libraries/Http/Assimalign.Cohesion.Http.Sessions/docs/DESIGN.md#diagnostics) |
| `Assimalign.Cohesion.Connections.Tcp` | Listener bound/closed; queued connection skipped because its client reset it, or on Linux a network error was pending on it, before the accept (Verbose); accept backed off for want of descriptors or buffers, or on Unix for an error .NET does not name (Warning, at most once a second per listener); accepted connection closed because its socket could not be set up (Verbose); connection opened/closed; peer end-of-stream, back-pressure pause/resume, reset (Verbose); connection error | `current-connections`, `total-connections`, `connections-per-second` | [Tcp DESIGN.md](../libraries/Connections/Assimalign.Cohesion.Connections.Tcp/docs/DESIGN.md#diagnostics) |
| `Assimalign.Cohesion.Connections.Quic` | Listener bound/closed; connection opened/closed; stream opened/closed (Verbose); inbound handshake failed and dropped | `current-connections`, `total-connections`, `connections-per-second`, `current-streams`, `streams-per-second` | [Quic DESIGN.md](../libraries/Connections/Assimalign.Cohesion.Connections.Quic/docs/DESIGN.md#diagnostics) |
| `Assimalign.Cohesion.Connections.NamedPipes` | Listener bound/closed; connection opened/closed | `current-connections`, `total-connections`, `connections-per-second` | [NamedPipes DESIGN.md](../libraries/Connections/Assimalign.Cohesion.Connections.NamedPipes/docs/DESIGN.md#diagnostics) |
| `Assimalign.Cohesion.Connections.Udp` | Datagram connection opened (bind or connect)/closed | `current-connections`, `total-connections` | [Udp DESIGN.md](../libraries/Connections/Assimalign.Cohesion.Connections.Udp/docs/DESIGN.md#diagnostics) |
| `Assimalign.Cohesion.DependencyInjection` | Provider built; call site built, service resolved, scope disposed, provider descriptors, resolver compiled (Verbose); resolver compilation failed | none | [DependencyInjection DESIGN.md](../libraries/DependencyInjection/Assimalign.Cohesion.DependencyInjection/docs/DESIGN.md#diagnostics) |
| `Assimalign.Cohesion.Web.Authentication.Bearer` | TokenRejected (Verbose): bounded rejection reason, algorithm/key id and request correlation; no token or claims | `rejected-tokens` | [Bearer DESIGN.md](../resources/Web/Assimalign.Cohesion.Web.Authentication.Bearer/docs/DESIGN.md#diagnostics) |
| `Assimalign.Cohesion.Web.Authentication.Cookie` | TicketRejected (Verbose): bounded reason and sanitized unprotect exception metadata with request correlation | `rejected-tickets` | [Cookie DESIGN.md](../resources/Web/Assimalign.Cohesion.Web.Authentication.Cookie/docs/DESIGN.md#diagnostics) |
| `Assimalign.Cohesion.Web.Authorization` | RequestDenied (Verbose): challenge/forbid outcome, route template, policy source, failed requirement type, schemes and request correlation | `challenged-requests`, `forbidden-requests` | [Authorization DESIGN.md](../resources/Web/Assimalign.Cohesion.Web.Authorization/docs/DESIGN.md#diagnostics) |
| `Assimalign.Cohesion.Web.Antiforgery` | RequestRejected and sanitized TokenUnprotectFailed (Verbose); RejectionAborted (Warning, at most once a second with suppressedCount); FormMalformed has no exception text | `rejected-requests` | [Antiforgery DESIGN.md](../resources/Web/Assimalign.Cohesion.Web.Antiforgery/docs/DESIGN.md#diagnostics) |
| `Assimalign.Cohesion.Web.Authentication.Certificate` | CertificateRejected (Verbose): NoCertificate on a required challenge or ValidationFailed from application mapping; no certificate identifiers. TLS trust/validity failures occur before HTTP and are unavailable to this handler | `rejected-certificates` | [Certificate DESIGN.md](../resources/Web/Assimalign.Cohesion.Web.Authentication.Certificate/docs/DESIGN.md#diagnostics) |
| `Assimalign.Cohesion.Web.Hosting.Resources` | Every area's shared HTTP control plane: throttled credential/loopback refusal and malformed-command events, stop accepted. Non-Web resource processes require a tool or their own forwarder; contributor failures are producer-owned below | `rejected-credentials` | [Web.Hosting.Resources DESIGN.md](../resources/Web/Assimalign.Cohesion.Web.Hosting.Resources/docs/DESIGN.md#diagnostics) |
| `Assimalign.Cohesion.Hosting.Resources` | Shared control-plane contributor failures and command outcomes: applied, unknown kind (501), conflict (409), handler failure. Only registered kinds are written; refusals/failures are throttled. HTTP surface events belong to Web.Hosting.Resources | `contributor-failures`, `commands-applied`, `unknown-command-kinds`, `command-conflicts`, `command-handler-failures` | [Hosting.Resources DESIGN.md](../libraries/Hosting/Assimalign.Cohesion.Hosting.Resources/docs/DESIGN.md#diagnostics) |
| `Assimalign.Cohesion.Web.Caching` | ResponseNotStored (Verbose); EntryDeclined (Warning, throttled); no cache keys or tags | `declined-entries`; hit/miss counters omitted after benchmarking | [Caching DESIGN.md](../resources/Web/Assimalign.Cohesion.Web.Caching/docs/DESIGN.md#diagnostics) |
| `Assimalign.Cohesion.Web.StaticFiles` | WebRootMounted; WebRootUnavailable; TraversalRejected (Verbose); MountLookupFailed (Warning, type only, throttled) | `traversal-rejections`, `mount-lookup-failures` | [StaticFiles DESIGN.md](../resources/Web/Assimalign.Cohesion.Web.StaticFiles/docs/DESIGN.md#diagnostics) |
| `Assimalign.Cohesion.Web.Sessions` | SessionOrphaned (Verbose), SessionStateLost (modified orphans only, Warning, throttled); store failures belong to Http.Sessions | `orphaned-sessions`, `lost-sessions` | [Sessions DESIGN.md](../resources/Web/Assimalign.Cohesion.Web.Sessions/docs/DESIGN.md#diagnostics) |
| `Assimalign.Cohesion.Web.WebSockets` | HandshakeRejected, WebSocketOpened, WebSocketClosed, DrainCloseFailed (all Verbose) | `current-websockets`, `total-websockets`, `rejected-handshakes` | [WebSockets DESIGN.md](../resources/Web/Assimalign.Cohesion.Web.WebSockets/docs/DESIGN.md#diagnostics) |
| `Assimalign.Cohesion.Web.Compression` | Request decompression refused (Verbose); decompressed size exceeded or rejection aborted after response commit (Warning, throttled) | `decompression-limit-rejections` | [Compression DESIGN.md](../resources/Web/Assimalign.Cohesion.Web.Compression/docs/DESIGN.md#diagnostics) |
| `Assimalign.Cohesion.Web.RateLimiting` | Rejected global or endpoint policy (Verbose); rejection after response commit (Warning, throttled); partition keys are never written | `rejected-requests` | [RateLimiting DESIGN.md](../resources/Web/Assimalign.Cohesion.Web.RateLimiting/docs/DESIGN.md#diagnostics) |
| `Assimalign.Cohesion.Web.Forms` | Form limit or malformed input (Verbose); rejection after response commit (Warning, throttled); configured limit names and exception types only | `form-limit-rejections`, `malformed-forms` | [Forms DESIGN.md](../resources/Web/Assimalign.Cohesion.Web.Forms/docs/DESIGN.md#diagnostics) |
| `Assimalign.Cohesion.Web.RequestTimeouts` | Timeout enforced (Warning, throttled) or ignored by downstream work (Verbose); armed interval and request correlation | `timed-out-requests`, `ignored-timeouts` | [RequestTimeouts DESIGN.md](../resources/Web/Assimalign.Cohesion.Web.RequestTimeouts/docs/DESIGN.md#diagnostics) |
| `Assimalign.Cohesion.Web.Cors` | Refused preflights and pass-through causes, with bounded origin and rejected header name (Verbose) | `rejected-preflights` | [Cors DESIGN.md](../resources/Web/Assimalign.Cohesion.Web.Cors/docs/DESIGN.md#diagnostics) |
| `Assimalign.Cohesion.Web.ForwardedHeaders` | Forwarding stop reason, family and applied depth (Verbose); transport peer IP only for an untrusted peer at depth zero | `untrusted-forwarding`, `malformed-forwarding` | [ForwardedHeaders DESIGN.md](../resources/Web/Assimalign.Cohesion.Web.ForwardedHeaders/docs/DESIGN.md#diagnostics) |
| `Assimalign.Cohesion.Web.HostFiltering` | Rejected effective host with bounded host component, reason and request id (Verbose) | `rejected-requests` | [HostFiltering DESIGN.md](../resources/Web/Assimalign.Cohesion.Web.HostFiltering/docs/DESIGN.md#diagnostics) |
| `Assimalign.Cohesion.Web.SecurityHeaders` | Security policy staging skipped by read-only headers (Warning, throttled); excludes disabled endpoints and protocol takeovers | `unprotected-responses` | [SecurityHeaders DESIGN.md](../resources/Web/Assimalign.Cohesion.Web.SecurityHeaders/docs/DESIGN.md#diagnostics) |
| `Assimalign.Cohesion.Web.Health` | Health worsened (Warning), improved (Informational), thrown or timed-out check (Verbose); includes adapted Hosting.Health contributors | `failed-checks` | [Health DESIGN.md](../resources/Web/Assimalign.Cohesion.Web.Health/docs/DESIGN.md#diagnostics) |

Deliberately not instrumented: `Connections.InMemory` (a test driver), `Connections.Security` (TLS
handshakes are already reported by the runtime's `System.Net.Security` source, and a handshake that
fails on a TLS-layered listener is reported by `Assimalign.Cohesion.Connections`, which owns the
listener that closes the connection), and
`Http.Connections` (requests are traced and measured by the Web server below, and connections are
counted by the drivers above; its empty placeholder source was deleted, see its
[DESIGN.md](../libraries/Http/Assimalign.Cohesion.Http.Connections/docs/DESIGN.md#diagnostics)).

### Web assemblies deliberately left uninstrumented

The Web scope deliberately leaves these 16 Web assemblies without an event source, plus the
framework producer shells. Each row records the reason from
[Web event-source scope §6](programs/WEB_EVENT_SOURCES_SCOPE.md#6-no-source-and-why); assemblies
scheduled to gain sources are not part of this list.

| Assembly or framework project | Why it has no source |
| --- | --- |
| `Assimalign.Cohesion.Web` | Contracts and pure pipeline helpers have no runtime state, loop or swallowed failure; the root must not claim every Web package's signals. |
| `Assimalign.Cohesion.Web.Server` | Contracts only; Web.Hosting implements and reports them, and #1300 owns the client-fault latch. |
| `Assimalign.Cohesion.Web.Hosting.Health` | A stateless adapter whose runtime failure throws; Web.Health reports contributor faults and timeouts. |
| `Assimalign.Cohesion.Web.Testing` | A shipped test harness, like Connections.InMemory, rather than a runtime component; it receives no access to internal sources. |
| `Assimalign.Cohesion.Web.ApplicationModel` | Invalid declarative plans throw; runtime control-plane outcomes belong to Hosting.Resources. |
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

| Assembly (= `ActivitySource` and `Meter`) | Traces | Metrics | Reference |
| --- | --- | --- | --- |
| `Assimalign.Cohesion.Web.Hosting` | One `Server` span per HTTP request, parented to the caller's `traceparent` | `http.server.request.duration`, `http.server.active_requests` | [Web.Hosting DESIGN.md](../resources/Web/Assimalign.Cohesion.Web.Hosting/docs/DESIGN.md#server-telemetry-1064) |

## Not yet conforming

These sources predate the convention. Their names will change when they are brought into line, so do
not build tooling against them.

| Assembly | Current name | Problem | Tracked by |
| --- | --- | --- | --- |
| `Assimalign.Cohesion.Resilience` | `AssimalignCohesionResilience` | Concatenated name; public constructor; no events. | #1038 |
| `Assimalign.Cohesion.Resilience.Retry` | *(empty)* | Empty name; an event method that writes nothing. | #1038 |
| `Assimalign.Cohesion.Resilience.Timeout` | `Assimalign.Cohesion.Resilience.TimeoutResilienceEventSource` | Type name in the source name; no events. | #1038 |
