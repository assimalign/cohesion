# Web Event Sources: Scope

**Date:** 2026-10-10. **Status:** scoped. Three owner decisions, (a), (d) and (f), are open.
**Standard:** `.claude/rules/event-source.md`. **Catalog:** `docs/EVENT_SOURCES.md`.
**Program:** HTTP/Web (`docs/programs/HTTP_WEB_PROGRAM_PLAN.md`). Recommended placement: Stage 14, lane G.
**Tracking:** epic #1396, tasks #1397–#1408, side defects #1409–#1413.

Claims that are not plain from the code are tagged **[Certain]** (checked against the code at
`5b51304b`), **[Likely]** (a strong inference) or **[Guessing]** (a gap filled by judgment).

## 1. Purpose and the standard

This document scopes how the Web area (`resources/Web/**`) reports on itself at run time.
`event-source.md` makes `System.Diagnostics.Tracing.EventSource` the single mechanism for a library's
lifecycle events, failures and counters. Each assembly that raises events owns exactly one
`internal sealed <Subject>EventSource`, named after the assembly, with exact counters, bounded
payloads and four required tests. A library never takes a logging dependency to describe itself.
Out-of-process tools (`dotnet-trace`, `dotnet-counters`, `dotnet-monitor`) enable a source by name.
In process, `Assimalign.Cohesion.Logging.EventSource` forwards events into `ILogger` sinks, and an
application opts into that. The scope covers 21 sources, 63 events and 35 counters. It also covers
the three owner decisions the migration forces, the work breakdown, its placement in the program,
and the defects found along the way.

## 2. Findings first

### 2.1 #147 contradicts the rule, and the rule never loaded where #147 was written

- **[Certain]** Web.Hosting began describing itself through `ILogger` in `f03ddafe` (2026-10-06, #147).
  It logs bind failure, accept-loop fault, connection fault and drain timeout through
  `Internal/WebApplicationServerLog.cs` and `WebApplicationServerOptions.Logger`. The rule arrived in
  `fc58efa6` on 2026-09-28, eight days earlier. Its first paragraph decides the question:
  "`EventSource` is the one mechanism: lifecycle events, failures, and counters ... a library never
  takes a logging dependency to describe its own behavior."
- **[Certain]** #147's body never asks for `ILogger`. The code carries no deviation marker.
  `Web.Hosting/docs/DESIGN.md:719` justifies the choice in words the rule rejects: "no `EventSource`:
  these are discrete events an operator acts on".
- **Why it happened [Certain]:** the rule's `paths:` frontmatter loads it only for
  `**/Internal/EventSource/**`, `**/*EventSource*.cs` and the forwarder package. A session editing
  `WebApplicationServer.cs` never sees it. `checklist.md` does load for every `.cs` file, but its
  line 43 covers only "any new or changed `EventSource`", so a session that adds `ILogger`
  self-description never triggers it. **[Likely]** #147's 2026-09-29 scope update ("nothing logs
  startup failures or drain timeouts") also read as an instruction to log.
- **Consequence:** following the standard means undoing #147. Decision (a) below.

### 2.2 No Web assembly has an event source today

**[Certain]** None of the 37 Web assemblies (39 projects, less the two framework producers) declares
an `EventSource`. The four surveys proposed 23 sources, 86 events and 51 counters. This scope keeps
21 sources, 63 events and 35 counters, with no keywords. The following failures leave no trace today:

- **Web.Hosting:**
  - a swallowed abort-grace expiry;
  - swallowed completion-callback failures;
  - refused response heads;
  - the control plane silently disabled.
- **Web.Hosting.Resources:** credential rejections.
- **Web.ErrorHandling:** application faults under a default `UseErrorHandling()`.
- **Web.Health:** a check that flaps or turns Unhealthy.
- **Web.Diagnostics:** a full disk, because `CompositeLogger` swallows provider exceptions.
- **Web.Cors:** a refused preflight, which every metric counts as a 204 success.
- **Web.SecurityHeaders:** a response that ships without CSP.

### 2.3 What "default visibility" means today

- **[Certain]** A plain Web application has **no logging provider**. `WebApplicationBuilder` creates an
  empty `LoggerFactoryBuilder` (`WebApplicationBuilder.cs:115`), and the `cohesion-web` template's
  `Program.cs` adds none. #147's entries reach a sink only in two cases:
  - the application added a provider;
  - the application is an orchestrated resource with telemetry on. `ResourceTelemetry.Configure` then
    adds the OTLP exporter, plus a JSON console when `LogFormat=json`.
- **[Certain]** The migration therefore costs entries in *configured* sinks, chiefly the OTLP export an
  orchestrated resource gets by default. It does not cost a console that never existed.
- **[Certain]** No SDK sets `EventSourceSupport`. A NativeAOT Web application therefore receives no
  events at all. #147's `ILogger` entries do reach a NativeAOT application's sinks today.

## 3. Owner decisions needed

### (a) Migrate #147 into the event source, and buy back default visibility

**Recommendation.** Migrate, then gate a default forwarder on the entry-point builder.

- **The migration:**
  - Delete `WebApplicationServerLog` and `WebApplicationServerOptions.Logger`.
  - #147's four entries become `WebServerEventSource` events 2, 3, 4/5 and 7 (§5.1).
  - Keep no second copy. An `ILogger` copy beside the source would write every entry twice once
    forwarding is on.
- **The default forwarder.** `WebApplicationBuilder.Build` registers `AddEventSourceForwarding` with
  these limits:
  - Only when `_isEntryPoint` is true, that is under `CreateBuilder(args)` (`WebApplicationBuilder.cs:109-112`).
  - Only when the application registered no forwarder of its own.
  - `Sources.Clear()`, then `Assimalign.Cohesion.Web.` only. Add #1300's exact source name once it
    lands, never the `Connections.` driver prefix.
  - With an opt-out.
- **[Certain] Why `Clear()`:** `EventSourceForwardingOptions.Sources` is pre-populated with
  `Assimalign.Cohesion.` (`EventSourceForwardingOptions.cs:36`), and a collection initializer adds to
  it. Without the clear, Connections.Tcp's Informational `ConnectionOpened`/`ConnectionClosed`
  (`TcpConnectionEventSource.cs:172`, `:176`) would put two entries per HTTP connection into every
  configured sink, including OTLP.
- **[Certain] Why the entry-point gate:** EventListeners are process-wide, so N forwarders write each
  event N times, into every host's factory.
  - 17 other area hosting modules build a nested `WebApplication.CreateBuilder()` for their admin or
    data plane (for example `DatabaseAdminEndpointService.cs:51`).
  - `WebApplicationTestFactory` builds one per test host with `CreateBuilder(options)`.
  - The gate excludes all of these.
- **What the gate does not exclude [Certain]:** `ProgramWebApplicationTestFactory` runs the consumer's
  `Program.Main(args)`, so concurrent program hosts in one test process each register a forwarder and
  see each other's Web events.

**Costs the owner accepts:**

- **Closure.** `Logging.EventSource` joins Web.Hosting's closure: App.Web, plus privately the 17 area
  frameworks that carry Web.Hosting. **[Likely]** One `CohesionAppKernelRoot` entry beside
  `Logging.Console` (`libraries/App/Assimalign.Cohesion.App.props:134`) is cheaper than 18 member-list
  edits.
- **Forwarder DESIGN.** It trips the forwarder DESIGN's packaging clause, "Revisit if a hosting root
  starts forwarding by default". That section is rewritten in the same change.
- **New public API.** The "no forwarder registered" check needs a probe on `ILoggerFactoryBuilder`,
  which does not exist today **[Certain]**. A second `AddEventSourceForwarding` makes `Build` throw,
  because forwarder names are unique.
- **Per-host attribution is lost.** #147 routes each entry into its own host's factory, while a
  process-wide forwarder cannot.
- **Nested hosts forward nothing by default.** Non-Web resource processes see control-plane events
  (§5.2) only through a tool or a forwarder of their own.
- **Stack traces.** `ConnectionFaulted` and `AcceptLoopFaulted` lose the stack traces #147's entries
  carry (rule 11). Keep the rule, and revisit only with evidence.
- **Names change.** The logger category moves from `Assimalign.Cohesion.Web.Hosting.WebApplicationServer`
  to `Assimalign.Cohesion.Web.Hosting`, and dotted attribute keys become camelCase payload names. That
  is a break, acceptable during the previews.
- **Inheritance.** Database.Hosting and the filler hosting modules inherit this decision when they get
  sources.

**Alternatives:**

1. **The `cohesion-web` template's `Program.cs` calls `builder.Logging.AddEventSourceForwarding()`.**
   - What it saves: no closure change, no probe API, no revisit-clause trip, and the opt-in stays
     visible in the code.
   - What it costs:
     - the template needs a `Logging.EventSource` package reference;
     - applications not made from the template get nothing;
     - forwarding does nothing until a provider exists. Orchestration adds one, and a plain
       template application has none.
   - **[Likely]** It is the cheaper option if the owner accepts that existing applications must opt in.
2. **Keep #147 as a recorded deviation.** Rejected: it takes the assembly with the most critical
   operator signals out of `dotnet-trace` and `dotnet-monitor`, and leaves two mechanisms.
3. **Keep both.** Rejected: once forwarding is on, every entry is written twice.

### (d) Payload hygiene, and one event-source.md amendment

**Recommendation.** One amendment to `event-source.md` in T0 rather than per-site deviations. It covers
the following.

- **Throttling.**
  - A Warning or Error that a remote client, a scanner or a misconfiguration can repeat at will is
    raised at most once per second per event, with `suppressedCount:int`. Tcp's `AcceptBackoff` is the
    precedent.
  - The event's counter stays exact and counts every occurrence.
  - Distinct application faults are not throttled: `PipelineFault`, `CompletionCallbackFailed`,
    `ConnectionFaulted`.
  - The one exception is `ResponseHeadRefused`, which header-injection probes drive.
- **Attacker-chosen identifiers.**
  - Keep one only where it is the diagnosis, and only at Verbose:
    - `Host`, 253 characters;
    - a normalized `Origin`, 256;
    - JWT `kid`, 128, and `alg`, 32;
    - a rejected header name, 64.
  - Truncate inside `IsEnabled` and treat the text as untrusted.
- **Correlation payload.**
  - Per-request events carry `requestId`, which is `IWebRequestIdFeature.RequestId.ToHexString()` read
    inside `IsEnabled`. It is the server span's trace id when the request is traced, and otherwise the
    `traceparent` id or a stable random id. #1297 makes the HTTP log record the same value, so events
    and the access log join.
  - **Never read `Activity.Current`.** **[Certain]** Web.Hosting clears it only inside `StartActivity`
    (`WebExchangeTelemetry.cs:244-249`), which runs only when the ActivitySource has a listener. With no
    listener, a host started inside an activity would stamp that one ambient trace id on every event.
  - **Cost [Certain]:** only 6 Web projects reference Web.Server, where the feature lives: Hosting,
    Hosting.Resources, ErrorHandling, Compression, Diagnostics and WebSockets. Fourteen sources add that
    contracts-only, same-area reference:
    - Authentication.Bearer, Authentication.Cookie, Authorization and Antiforgery;
    - Cors, SecurityHeaders, HostFiltering and ForwardedHeaders;
    - RateLimiting and RequestTimeouts;
    - Caching, StaticFiles, Sessions and Forms.

    Web.Server is already an App.Web member (`Web.Runtime/Directory.Build.props:68`), so no framework
    list changes. The alternative is to drop the field.
- **Exception messages that echo request input.**
  - Rule 11 says type plus `Message`. The amendment adds a type-only carve-out where the message is
    known to echo request input:
    - Http.Forms' `InvalidDataException` (`HttpMultipartFormReader.cs:215` quotes the client's header line);
    - StaticFiles' `FileSystemException`;
    - application-delegate exceptions in Web.Diagnostics;
    - JSON and handler exceptions in control-plane commands.
  - Application exception messages stay type plus `Message`, and each owning DESIGN says so. That
    covers ErrorHandling, completion callbacks and health checks.
  - **The owner must accept this explicitly:** under default forwarding, a default
    `UseErrorHandling()` now writes every application exception message to the configured sinks. .NET
    `FormatException` messages quote their input, and #147 logged no pipeline exception at all.
- **Logger-category prefix overlap.** The source name is the logger category. So
  `Assimalign.Cohesion.Web.Diagnostics` (the source) is a prefix of
  `Assimalign.Cohesion.Web.Diagnostics.HttpLogging` (the HTTP log's default category,
  `HttpLoggingOptions.cs:44`), and a filter rule on one also tunes the other. Document it, and add no
  default filter rule.
- **Widen the rule's reach.**
  - Add `libraries/**/src/**/*.cs` and `resources/**/src/**/*.cs` to `event-source.md`'s `paths:`.
  - Extend `checklist.md:43` to say a library reports on itself only through its internal
    `EventSource`, never `ILogger`.
  - Without this, the conformance test (T11) catches a misplaced source but not the next `ILogger`
    self-report.
- **Two smaller points.**
  - Per-request events are Verbose and never report success.
  - A same-named `ActivitySource` and `Meter` split the signals: spans and `http.server.*` metrics in
    the BCL types, operator events and attribution counters in the `EventSource`.

**Never written by any Web source:**

- `url.path`, the query or the raw target;
- header values, bodies or form fields;
- cookie or session values, tokens, claims, user names or roles;
- rate-limit partition keys, cache keys or tag names;
- control-plane command payloads, keys or caller identities.

Routes appear as `routeTemplate` (`IWebEndpointFeature.RouteTemplate`), and only in packages that
already reference Web.Routing. A peer address appears only in `ConnectionFaulted`/`ConnectionEndedByPeer`,
`CredentialRejected` and `ForwardingStopped(UntrustedPeer)`, and it is personal data in the sink.

**Alternatives.**

- Five recorded per-site deviations instead of one amendment. That is the same outcome, scattered.
- Drop `exceptionMessage` everywhere. That loses the diagnosis for application faults.

**Cost.**

- A rule change, which needs the owner's confirmation.
- 14 project references.
- Application exception text in sinks.

### (f) Sdk.Web turns on EventSourceSupport for NativeAOT

**Recommendation.** Sdk.Web sets
`<EventSourceSupport Condition="'$(EventSourceSupport)' == ''">true</EventSourceSupport>` in
`sdks/Assimalign.Cohesion.Sdk.Web/Targets/Sdk.Web.props`. Without it, the #147 migration removes the
only server diagnostics a NativeAOT Web application has.

Two steps, in order:

1. **Measure first.** Publish the Web AOT guard for linux-x64 with and without the setting, and record
   the binary-size delta in the Sdk.Web docs before the owner decides. **[Guessing]** The setting also
   keeps the runtime's own sources, so the delta may be noticeable.
2. **Add an Sdk.Web evaluation test.** It asserts that `EventSourceSupport` evaluates to `true` under
   `PublishAot=true` unless the project sets it. `sdks/Assimalign.Cohesion.Sdk.ApplicationModel/Tasks/tests/ResourceAreaDefaultsTests.cs`
   is the precedent for area-SDK default tests **[Likely]**; the package-only `SdkSmoke` consumer in
   `cohesion-examples` is the fallback.
   - **[Certain]** The Web AOT guard cannot prove the SDK default: it is `<Project Sdk="Microsoft.NET.Sdk">`.
     It keeps its own explicit `EventSourceSupport=true` for the delivery check.

**Alternatives.**

- Leave it opt-in and document it in `docs/EVENT_SOURCES.md` ("NativeAOT"). AOT Web applications then
  lose #147's diagnostics after the migration.
- Default it in the base `Assimalign.Cohesion.Sdk`. That widens the cost to every area.

**Cost:** binary size, plus a startup cost not yet measured **[Guessing]**.

## 4. Conventions every source follows

- **Shape.** An `internal sealed class <Subject>EventSource` in `src/Internal/EventSource/`, in namespace
  `{RootNamespace}.Internal`, named exactly for the assembly. **[Certain]** Forms is the one irregular
  RootNamespace (`Assimalign.Cohesion.Web`). Its source lives in `Assimalign.Cohesion.Web.Internal` and
  is named `Assimalign.Cohesion.Web.Forms`.
- **Payload notation.** In the tables, fields are strings unless a type follows the colon. Every
  `ToString`, `RouteTemplate` read and `requestId` read happens inside `IsEnabled`.
- **No keywords.** **[Certain]** The forwarder enables `EventKeywords.All` and does not filter by
  keyword (`EventSourceLogForwarder.cs:169`), and no Cohesion source defines keywords. Tools select by
  level.
- **Throttled** means at most one event per second per event id, with `suppressedCount:int` (§3 (d)).
- **No `Start`/`Stop` names.** Lifetimes that cross flows use `Bound`/`Opened`/`Closed`/`Stopped`
  (rule 8).
- **Counters** are `PollingCounter` (totals and gauges) or `IncrementingPollingCounter` (rates). They
  are created in `OnEventCommand`, backed by `Interlocked` fields maintained whether or not anyone
  listens, and exposed as `internal` read-only properties for tests.

## 5. Per-assembly design

Paths are relative to each project folder, `resources/Web/<Assembly>/`.

### 5.1 Assimalign.Cohesion.Web.Hosting: `WebServerEventSource`

The same name as the #1064 ActivitySource and Meter, so one name enables all three. It replaces
`Internal/WebApplicationServerLog.cs` and `WebApplicationServerOptions.Logger`.

`serverId:long` is a process-unique sequence assigned in the `WebApplicationServer` constructor with a
static `Interlocked.Increment`. **[Certain]** The server has no identifier today, and one process can run
several servers (Database's admin plane, `AddServer`). `ServerBound` carries the bound endpoints, so a
tool maps `serverId` to endpoints.

| Id | Name | Level | Payload | Raised from | Throttled |
|---|---|---|---|---|---|
| 1 | ServerBound | Informational | serverId:long, protocols, endPoints (comma-joined), maxConcurrentConnections:int (0 = unlimited) | `src/Internal/WebApplicationServer.cs:185-196`, after the bind | no |
| 2 | BindFailed | Critical | serverId:long, protocols, exceptionType, exceptionMessage | `WebApplicationServer.cs:172-183`, before `HostStartupException` propagates | no |
| 3 | AcceptLoopFaulted | Error | serverId:long, exceptionType, exceptionMessage | `WebApplicationServer.cs:414-421` | no |
| 4 | ConnectionFaulted | Error | serverId:long, connectionId, localEndPoint, remoteEndPoint, protocolVersion (1.1, 2, 3 or empty), exceptionType, exceptionMessage | `WebApplicationServer.cs:477-488` and `:507-512`, for a defect: not `IOException`, `SocketException` or `ConnectionException`, and not after an aborted drain | no |
| 5 | ConnectionEndedByPeer | Verbose | as event 4 | the same two sites, for transport-classified faults or a fault after an aborted drain (#147's Debug entry) | no |
| 6 | ServerDraining | Informational | serverId:long, connections:int, exchanges:int | `WebApplicationServer.cs:242`, after `_drain.Begin()` | no |
| 7 | DrainTimedOut | Warning | serverId:long, connections:int, exchanges:int, elapsedMilliseconds:double | `WebApplicationServer.cs:255-265` | no (once per stop) |
| 8 | AbortGraceExpired | Warning | serverId:long, exchanges:int, gracePeriodMilliseconds:double | `WebApplicationServer.cs:353-359`. The `TimeoutException` is swallowed today | no (once per stop) |
| 9 | ServerStopped | Informational | serverId:long, elapsedMilliseconds:double, aborted:bool | `WebApplicationServer.cs:273-286`, after the listener's `DisposeAsync` | no |
| 10 | ResponseHeadRefused | Error | serverId:long, requestId, protocolVersion, routeTemplate, exceptionMessage, suppressedCount:int | `WebApplicationServer.cs:564-572`, the #1183 catch that replaces the head with a 500 | yes |
| 11 | CompletionCallbackFailed | Error | serverId:long, requestId, routeTemplate, exceptionType, exceptionMessage | `WebApplicationServer.cs:759-771`. `RunCompletionCallbacksAsync` swallows today | no |
| 12 | ExchangeIsolationEscaped | Error | exceptionType, exceptionMessage | `src/Internal/MultiplexedExchangeTracker.cs:80`, the last-guard catch | no |
| 13 | ConnectionCapReached | Verbose | serverId:long, maxConcurrentConnections:int | `WebApplicationServer.cs:371-374`, when a `Wait(0)` probe fails before `WaitAsync` parks the accept loop | no |
| 14 | ControlPlaneDisabled | Warning | resourceName, observedEndpoints (comma-joined names) | `src/WebApplicationBuilder.cs:298-300`: a control plane is set, but no http or https endpoint was observed | no (once per pipeline) |
| 15 | TelemetryListenerFaulted | Warning | exceptionType, exceptionMessage, suppressedCount:int | `src/Internal/WebExchangeTelemetry.cs:119`, `:195`, `:358` | yes |

| Counter | Kind | Why it is exact |
|---|---|---|
| `connection-faults` | PollingCounter, total | Incremented when event 4's condition holds. A per-connection local flag counts a connection once when it reaches both `:477` and `:507`. Peer-ended connections (event 5) are not counted. |
| `connection-cap-waits` | PollingCounter, total | One increment each time the `Wait(0)` probe fails. There is no available-slots gauge, because several servers can share a process. |
| `refused-response-heads` | PollingCounter, total | There is a single #1183 catch site at `:564`. |

Notes:

- **Levels.**
  - `AcceptLoopFaulted` is Error, not #147's Critical. The server keeps running and the host is not
    stopped (`:414-421`), and rule 7 reserves Critical for a process that cannot continue. Whether the
    fault should stop the host, which would make Critical true, is a behavior question for #1079's
    owner.
  - `ResponseHeadRefused` is Error: it is an application fault turned into a 500 that ErrorHandling
    never sees. It stays throttled because header-injection probes drive it.
  - `ConnectionCapReached` is Verbose: reaching a configured cap is back-pressure working, like Tcp's
    pause and resume events, and `connection-cap-waits` carries the saturation.
- **Not reported here:**
  - Request counters and per-request events. The #1064 Meter and the server span own requests.
  - Pre-dispatch transport rejections and client-fault 4xx. #1300 owns those.
  - Pipeline exceptions. ErrorHandling reports those, as #147 decided.
- **`TelemetryListenerFaulted`** uses the standard throttle rather than a once-per-process latch, so a
  tool that attaches later still sees a listener that keeps failing.
- **Tests and docs.** The 8 `WebApplicationServerDiagnosticsTests` are rewritten against an
  `EventListener`. DESIGN "Diagnostics (#147)" is rewritten and the `DESIGN.md:719` rationale retired.

### 5.2 Assimalign.Cohesion.Web.Hosting.Resources: `ResourceControlPlaneEventSource`

**[Certain]** The source reports every area's control plane, identified by `resourceName`, not only Web's.

- 13 other area hosting modules install `ResourceControlPlaneMiddleware` on a nested
  `CreateBuilder()` host: ApiManager, Database, EmailHub, EventHub, IoTHub, LoadBalancer, LogSpace,
  MediaHub, MessageHub, NatGateway, NotificationHub, Rezolvr and VpnGateway. For example,
  `DatabaseAdminEndpointService.cs:84`.
- None of those hosts is an entry-point builder, so none of them gets default forwarding (§3 (a)).

| Id | Name | Level | Payload | Raised from | Throttled |
|---|---|---|---|---|---|
| 1 | CredentialRejected | Warning | resourceName, status (Unauthorized or Forbidden), reason (missing-credential, unsupported-scheme, invalid-token, audience-mismatch, caller-mismatch or verifier-rejected), route (stop, commands, endpoints, healthz, readyz, livez or unknown), remoteEndPoint, suppressedCount:int | `src/ResourceControlPlaneMiddleware.cs:78-89`. The reason is threaded from `AuthorizeAsync` `:454-472`, `VerifyAsync` `:474-496` and `BootstrapTokenVerifier.cs:42-56` | yes |
| 2 | CommandMalformed | Warning | resourceName, httpMethod (POST or DELETE), phase (Envelope or Handler), exceptionType, suppressedCount:int | `ResourceControlPlaneMiddleware.cs:346-388`. `phase` is Handler once the try reaches `ExecuteCommandAsync`/`DeleteCommandAsync` at `:370-372` | yes |
| 3 | StopAccepted | Informational | resourceName, deferred:bool | `ResourceControlPlaneMiddleware.cs:159-181` | no |

| Counter | Kind | Why it is exact |
|---|---|---|
| `rejected-credentials` | PollingCounter, total | There is a single not-Authorized branch at `:79`, reached once per request. |

Notes:

- **Event 2 is renamed and split by phase.**
  - The survey called it `CommandEnvelopeRejected`. **[Certain]** The same try wraps the registered
    command handlers, so a handler's `JsonException`, `FormatException` or `ArgumentException` is
    answered 400 too.
  - `phase` keeps the two faults apart. Whether a handler's `ArgumentException` deserves a 400 is a
    behavior question, filed with the follow-ups.
- **Event 2 is throttled [Likely].** With no gateway identity the control plane is unauthenticated by
  design (DESIGN: "A missing gateway identity enables standalone execution"), so any client on the
  served port can drive it.
- **Payload limits.**
  - Never written: the `Authorization` header, the token, unverified claims, the caller's application
    or subject, the command key, or the payload.
  - The command payload is base64 that can carry secrets (`secretstore.add-secret`).
  - Event 2 writes the exception type only.
- **Command outcomes** (501, 409, applied) belong to a future Hosting.Resources source and are not
  reported here.
- **Grants.** The project needs a test-only `InternalsVisibleTo`, never one to Web.Testing (rule 3).

### 5.3 Assimalign.Cohesion.Web.ErrorHandling: `ErrorHandlingEventSource`

It becomes the documented owner of application-fault reporting, which `Web.Hosting/docs/DESIGN.md:760-763`
delegates to this package.

| Id | Name | Level | Payload | Raised from | Throttled |
|---|---|---|---|---|---|
| 1 | PipelineFault | Error | exceptionType, exceptionMessage, responseStarted:bool, requestId | `src/Internal/ExceptionBoundaryMiddleware.cs:82-104`, for application faults: no `IWebClientFaultFeature` status, not suppressed, and not a peer abort | no |
| 2 | ExpectedPipelineFault | Verbose | as event 1 | `:99-104`, when `SuppressDiagnosticsCallback` returns true. It is evaluated once, and the result is shared with `ObserveAsync` `:145-150` | no |
| 3 | ErrorHandlerFailed | Error | handlerIndex:int, handlerType, exceptionType, exceptionMessage, requestId | an exception filter around `TryHandleAsync` at `:131` and `src/Internal/ErrorHandlingFeature.cs:38`. The filter returns false, so the exception still propagates | no |
| 4 | DiagnosticsCallbackFailed | Warning | callback (OnException or SuppressDiagnosticsCallback), exceptionType, exceptionMessage, requestId | the empty catches at `:160-162` and `:179-182` | no |
| 5 | ResponseAbortedByPeer | Verbose | exceptionType, exceptionMessage, requestId | `:82-110`: no client-fault status, the response has started, and the exception is an `IOException` or `SocketException` | no |

| Counter | Kind | Why it is exact |
|---|---|---|
| `total-faults` | PollingCounter, total | One increment per application fault on the single catch path at `:82`, counted before suppression. Client faults, peer aborts and the request-cancel rethrow at `:74-79` are excluded. |
| `faults-per-second` | IncrementingPollingCounter | A rate over the same exact field. |
| `aborted-responses` | PollingCounter, total | Incremented once on the `HasStarted` branch at `:107-110`, for application faults only. Peer aborts are excluded. |

Notes:

- **Why event 5 exists [Certain].** HTTP/1.1 enforces a minimum response data rate by default, 240 B/s
  after a 5 s grace (`HttpConnectionListenerLimits.cs:195-221`). A client that reads below it makes the
  streaming write throw `IOException` (`Http1ResponseBodyStream.cs:191-196`) out of the handler and into
  this catch. `IWebClientFaultFeature` covers only request-body faults (`IWebClientFaultFeature.cs:29-42`).
  Without event 5, any slow reader would raise an unthrottled Error and count as an application fault.
- **[Certain] `ConnectionException` is not classified here.** It derives from `Exception`, not
  `IOException`, and ErrorHandling does not reference Connections. The durable fix is a response-side
  client-fault latch, added to #1384's and #1378's acceptance (§7), after which event 5 reads the latch
  instead of sniffing types.
- **Never written:** the path held by `IHttpExceptionFeature`, the query, the method or header values.
- **Double logging.** An application whose `OnException` hook logs, and which also forwards, gets two
  entries per fault. Document it.

### 5.4 Assimalign.Cohesion.Web.Authentication.Bearer: `JwtBearerEventSource`

| Id | Name | Level | Payload | Raised from | Throttled |
|---|---|---|---|---|---|
| 1 | TokenRejected | Verbose | scheme, reason (EmptyCredential, Malformed, NotCompactJws, UnsecuredAlgorithm, SignatureEncoding, NoMatchingKey, SignatureInvalid, ValidationFailed, IssuerRejected or AudienceRejected), algorithm (32), keyId (128), validationCode, issuer (256, IssuerRejected only), requestId | `src/Internal/JwtBearerHandler.cs:68`, `:73`, `:81` (signature exits `:135`, `:140`, `:150`, `:164`), `:89`, `:94`, `:99` | no |

| Counter | Kind | Why it is exact |
|---|---|---|
| `rejected-tokens` | PollingCounter, total | Once per request and scheme: the handler caches its result (`:48`), and the handler is cached per scheme per request (`DefaultAuthenticationService.cs:129-139`). NoResult is not counted. |

Notes:

- **Internal change.** Split the verifier loop at `:156-164` into NoMatchingKey (IdP key-rotation lag)
  and SignatureInvalid (tampering).
- **Never written:** the token, the `Authorization` header, any claim value, or
  `TokenValidationDiagnostic.Message`, which interpolates values.
- **Cut:** a validated-tokens counter, which would put an `Interlocked` on every authenticated request.
- **Future template.** #1305's Web.Authentication.Certificate takes this source as its template.

### 5.5 Assimalign.Cohesion.Web.Authentication.Cookie: `CookieAuthenticationEventSource`

| Id | Name | Level | Payload | Raised from | Throttled |
|---|---|---|---|---|---|
| 1 | TicketRejected | Verbose | scheme, reason (NotBase64Url, UnprotectFailed, PayloadMalformed or Expired), exceptionType, exceptionMessage (UnprotectFailed only; DataProtection's fixed strings), innerExceptionType (only when the key repository read failed), requestId | `src/Internal/CookieAuthenticationHandler.cs:83`, `:94`, `:104`, `:111` | no |

| Counter | Kind | Why it is exact |
|---|---|---|
| `rejected-tickets` | PollingCounter, total | Once per request and scheme, by the same caching argument as Bearer. |

Notes:

- **Never written:** the cookie value, the plaintext, the principal or the return URL.
- **Why Verbose.** Any client can forge an unknown-key cookie, so a lost key ring cannot rise above
  Verbose here. The Warning-grade alarm belongs to a Security.DataProtection source, a follow-up.
- **Cut:** `TicketRenewed`.

### 5.6 Assimalign.Cohesion.Web.Authorization: `AuthorizationEventSource`

| Id | Name | Level | Payload | Raised from | Throttled |
|---|---|---|---|---|---|
| 1 | RequestDenied | Verbose | outcome (Challenge or Forbid), routeTemplate, policySource (Endpoint or Fallback), failedRequirement (requirement type name), schemes, requestId | `src/Internal/AuthorizationMiddleware.cs:111-118` | no |

| Counter | Kind | Why it is exact |
|---|---|---|
| `challenged-requests` | PollingCounter, total | The single if/else decision at `:111-118`. |
| `forbidden-requests` | PollingCounter, total | The same decision point, else branch. |

Notes:

- **Internal change.** An internal evaluation returns the index of the first failing requirement. The
  public `EvaluateAsync` is unchanged, and the name is resolved only inside `IsEnabled`.
- **Why it matters.** Today a cookie-scheme challenge (a 302) cannot be told from an application
  redirect in `http.server.*` metrics. This source tells them apart.
- **Never written:** the user, claims, roles or route values.

### 5.7 Assimalign.Cohesion.Web.Antiforgery: `AntiforgeryEventSource`

| Id | Name | Level | Payload | Raised from | Throttled |
|---|---|---|---|---|---|
| 1 | RequestRejected | Verbose | reason (TokenInvalid, FormMalformed or FormTooLarge), statusCode:int, tokenLocation (Header, Form or None), routeTemplate, requestId | `src/Internal/AntiforgeryMiddleware.cs:58-61`, with the cause from `:88-103` | no |
| 2 | RejectionAborted | Warning | reason, routeTemplate, suppressedCount:int | `AntiforgeryMiddleware.cs:135-139`: the head was already committed, so the exchange is cancelled | yes |
| 3 | TokenUnprotectFailed | Verbose | exceptionType, exceptionMessage (DataProtection's fixed strings), innerExceptionType | `src/Internal/DataProtectionAntiforgeryProtector.cs:52` | no |

| Counter | Kind | Why it is exact |
|---|---|---|
| `rejected-requests` | PollingCounter, total | A single `RejectAsync` call site at `:61`. |

Notes:

- **FormMalformed carries no exception text.** Http.Forms quotes the client's multipart header line
  (`HttpMultipartFormReader.cs:215`).
- **Cut:** an unprotect-failures counter, which would count attempts (up to two per request) rather
  than requests.
- **Not reported here:** a body the client cut short, which belongs to #1340.

### 5.8 Assimalign.Cohesion.Web.Cors: `CorsEventSource`

| Id | Name | Level | Payload | Raised from | Throttled |
|---|---|---|---|---|---|
| 1 | PreflightRejected | Verbose | reason (OriginNotAllowed, MethodNotAllowed or HeaderNotAllowed), origin (a single Origin value, 256; empty when absent or repeated), requestedMethod (32), rejectedHeader (the first disallowed name, 64), routeTemplate, requestId | `src/Internal/CorsResponseHeaders.cs:128-130`, via `CorsMiddleware.cs:102` | no |
| 2 | PreflightPassedThrough | Verbose | cause (OptionsRouteOwnsPreflight, CorsDisabled or NoPolicy), requestedMethod, routeTemplate, requestId | `src/Internal/CorsMiddleware.cs:89`, `:99` | no |

| Counter | Kind | Why it is exact |
|---|---|---|
| `rejected-preflights` | PollingCounter, total | One increment in `WritePreflight`'s reject branch. |

Notes:

- **Internal change.** The combined check becomes an evaluator that returns a reason and allocates
  nothing on the success path.
- **Cut:** `OriginRefused`. Browsers also send `Origin` on same-origin POSTs, so a refusal cannot be
  told from a harmless request.

### 5.9 Assimalign.Cohesion.Web.SecurityHeaders: `SecurityHeadersEventSource`

| Id | Name | Level | Payload | Raised from | Throttled |
|---|---|---|---|---|---|
| 1 | HeadersNotApplied | Warning | statusCode:int, routeTemplate, committedBeforeTurn:bool, suppressedCount:int, requestId | `src/Internal/SecurityHeadersMiddleware.cs:79-82` (HasStarted and not staged) and `:94` (`Headers.IsReadOnly` at staging, also reached from `SecurityHeadersStreamingFeature.cs:83`) | yes |

| Counter | Kind | Why it is exact |
|---|---|---|
| `unprotected-responses` | PollingCounter, total | The two skip sites are mutually exclusive per response, and `:79` counts only the HasStarted-and-not-staged branch. |

Notes:

- **Exclusions.** 304 responses and `DisableSecurityHeaders` endpoints are excluded.
- **Upgrades [Guessing].** A 101 or extended-CONNECT upgrade may commit its head outside the streaming
  decorator. Exclude 101 responses and upgraded exchanges, and check how Web.WebSockets commits its head
  before shipping.
- **Ordering.** #1156's response-starting hook changes how this package stages headers, so this source
  lands after it.

### 5.10 Assimalign.Cohesion.Web.HostFiltering: `HostFilteringEventSource`

| Id | Name | Level | Payload | Raised from | Throttled |
|---|---|---|---|---|---|
| 1 | RequestRejected | Verbose | reason (EmptyHost or HostNotAllowed), host (`HttpHost.Host` without its port, 253; empty for EmptyHost), forwarded:bool, requestId | `src/Internal/HostFilteringMiddleware.cs:72` | no |

| Counter | Kind | Why it is exact |
|---|---|---|
| `rejected-requests` | PollingCounter, total | Every rejection passes the single point at `:72`. |

Notes:

- **The host is untrusted.** It is attacker-chosen wire text, and Host-header injection is the threat
  this guard exists for.
- **No routeTemplate.** The middleware runs before routing.
- **Why Verbose.** Scanners make this event high-volume.

### 5.11 Assimalign.Cohesion.Web.ForwardedHeaders: `ForwardedHeadersEventSource`

| Id | Name | Level | Payload | Raised from | Throttled |
|---|---|---|---|---|---|
| 1 | ForwardingStopped | Verbose | reason (UntrustedPeer, ForwardLimit, MalformedFor, InvalidProto, ImplausibleHost, UnusableForwardedHeader or AddresslessHop), family (Forwarded or X-Forwarded), hopsApplied:int, untrustedPeer (the transport peer IP, UntrustedPeer at depth 0 only), requestId | `src/Internal/ForwardedHeadersResolver.cs:147-150`, `WalkForwarded` `:164-186` and `:210-216`, `WalkXForwarded` `:244-281` and `:297-302`. Raised only when forwarding headers were present | no |

| Counter | Kind | Why it is exact |
|---|---|---|
| `untrusted-forwarding` | PollingCounter, total | `Resolve` runs once per exchange, and each walk has one exit. |
| `malformed-forwarding` | PollingCounter, total | The same single-exit argument. |

Note: header values are never written. They hold client addresses and hosts, which are personal data
and attacker-chosen.

### 5.12 Assimalign.Cohesion.Web.RateLimiting: `RateLimitingEventSource`

| Id | Name | Level | Payload | Raised from | Throttled |
|---|---|---|---|---|---|
| 1 | RequestRejected | Verbose | gate (global or endpoint), policyName, retryAfterSeconds:double (-1 when none), routeTemplate, requestId | `src/Internal/RateLimitingMiddleware.cs:111-116` | no |
| 2 | RejectionAborted | Warning | gate, policyName, suppressedCount:int | `RateLimitingMiddleware.cs:144-148`: the head had already started, then `CancelAsync` | yes |

| Counter | Kind | Why it is exact |
|---|---|---|
| `rejected-requests` | PollingCounter, total | A single rejection branch at `:111`, once per gate. |

Notes:

- **Never written:** the partition key, which is a client IP, a header value or an identity.
- **No admit-path counter.**
- **Follow-ups.** This closes the operator half of the `DESIGN.md:168-177` follow-up. OTel-convention
  instruments stay with #317.

### 5.13 Assimalign.Cohesion.Web.RequestTimeouts: `RequestTimeoutEventSource`

| Id | Name | Level | Payload | Raised from | Throttled |
|---|---|---|---|---|---|
| 1 | RequestTimedOut | Warning | timeoutMilliseconds:double, responseStarted:bool, statusCode:int (0 when aborted), routeTemplate, suppressedCount:int, requestId | `src/Internal/RequestTimeoutMiddleware.cs:94-97` (converted to 504) and `:124-127` (head committed, exchange reset) | yes |
| 2 | TimeoutIgnored | Verbose | timeoutMilliseconds:double, routeTemplate, requestId | a new check after the await at `:92` returns normally while `feature.TimedOut` is true | no |

| Counter | Kind | Why it is exact |
|---|---|---|
| `timed-out-requests` | PollingCounter, total | The catch filter runs at most once per exchange. |
| `ignored-timeouts` | PollingCounter, total | The post-await check runs only on normal completion. |

Notes:

- **Internal change.** `RequestTimeoutFeature` records the armed interval.
- **Raising flow.** Events are raised on the request flow, never in the timer callback.
- **Determinism.** Tests stay deterministic through the existing `TimeProvider`.

### 5.14 Assimalign.Cohesion.Web.Caching: `OutputCacheEventSource`

| Id | Name | Level | Payload | Raised from | Throttled |
|---|---|---|---|---|---|
| 1 | ResponseNotStored | Verbose | reason (BodyOverCap, StatusNotCacheable, SetCookie, CacheControlForbids, VaryStar, NoFreshness or DecidedBeforeRouting), routeTemplate, requestId | `src/Internal/OutputCacheMiddleware.cs:139-144` and `:206-227`. `IsResponseCacheable` (`:285-315`) returns the reason | no |
| 2 | EntryDeclined | Warning | entryBytes:long, sizeLimitBytes:long, suppressedCount:int | `src/InMemoryOutputCacheStore.cs:115-119`. The `CacheException` is swallowed today | yes |

| Counter | Kind | Why it is exact |
|---|---|---|
| `cache-hits` | PollingCounter, total | One increment on the serve branch. |
| `cache-misses` | PollingCounter, total | One increment on the no-servable-entry branch. |
| `declined-entries` | PollingCounter, total | Incremented in the `CacheException` catch, once per `SetAsync`. |

Notes:

- **Benchmark the hot-path counters.** `cache-hits` and `cache-misses` add a contended `Interlocked` to
  every cacheable GET. Benchmark them, and drop the pair if they exceed the agreed budget.
- **Never written:** the cache key or tag names.

### 5.15 Assimalign.Cohesion.Web.Compression: `CompressionEventSource`

The source covers request decompression only.

| Id | Name | Level | Payload | Raised from | Throttled |
|---|---|---|---|---|---|
| 1 | DecompressionRejected | Verbose | reason (UnsupportedCoding or MalformedContent), codings (recognized tokens only), exceptionType (MalformedContent only), requestId | `src/Internal/RequestDecompressionMiddleware.cs:64-69` (415) and `:98-101` (400) | no |
| 2 | DecompressionLimitExceeded | Warning | limitBytes:long, codings, suppressedCount:int, requestId | `RequestDecompressionMiddleware.cs:94-97`: a decompression bomb refused with 413 | yes |
| 3 | RejectionAborted | Warning | statusCode:int, suppressedCount:int | `RequestDecompressionMiddleware.cs:170-174` | yes |

| Counter | Kind | Why it is exact |
|---|---|---|
| `decompression-limit-rejections` | PollingCounter, total | Counted in the middleware catch at `:94`, once per exchange. It is not counted at the throw site (`LimitedDecompressionStream.cs:119`), where a retrying handler can trip it twice. |

Note: the raw `Content-Encoding` is never written. This 413 is the one the Meter cannot tell from a
transport body-cap 413.

### 5.16 Assimalign.Cohesion.Web.StaticFiles: `StaticFilesEventSource`

| Id | Name | Level | Payload | Raised from | Throttled |
|---|---|---|---|---|---|
| 1 | WebRootMounted | Informational | webRoot, prefix | `src/Internal/WebRootStaticFiles.cs:21-31` | no |
| 2 | WebRootUnavailable | Warning | webRoot (empty when unset), consumer (UseStaticFiles or MapFallbackToFile) | `WebRootStaticFiles.cs:23` (null return), seen from `WebApplicationStaticFilesExtensions.cs:54` and `StaticFilesFallbackExtensions.cs:74` | no (once per pipeline) |
| 3 | TraversalRejected | Verbose | reason (UnsafeSegment or InvalidPath), prefix, requestId | `src/Internal/StaticFilesMiddleware.cs:99-103`, `:116-121` | no |
| 4 | MountLookupFailed | Warning | prefix, exceptionType, suppressedCount:int | `src/Internal/StaticFilePath.cs:223`. The `FileSystemException` is swallowed today | yes |

| Counter | Kind | Why it is exact |
|---|---|---|
| `traversal-rejections` | PollingCounter, total | Each 404 branch runs once and returns. |
| `mount-lookup-failures` | PollingCounter, total | A single catch, once per `TryGetInfo`. |

Notes:

- **Type only for event 4.** `FileSystemException.Message` can hold the client's segment.
- **Branched pipelines.** Confirm that branched pipelines do not run `TryCreate` again.
- **Ordering.** Sequence this after Stage 12's #1324.

### 5.17 Assimalign.Cohesion.Web.Sessions: `WebSessionEventSource`

| Id | Name | Level | Payload | Raised from | Throttled |
|---|---|---|---|---|---|
| 1 | SessionOrphaned | Verbose | reason (ResponseStarted), requestId | `src/Internal/WebSessionFeature.cs:117-120`, decided at `:170` and `:196-199` | no |

| Counter | Kind | Why it is exact |
|---|---|---|
| `orphaned-sessions` | PollingCounter, total | Counted at the orphan check in `CommitAsync`, which `SessionMiddleware.cs:48-51` calls at most once per exchange. |

Notes:

- **[Certain] Why not a Warning.** `WasAccessed` is `_current is not null` (`WebSessionFeature.cs:64`),
  and reading `context.Session` materializes a new session (`:157-176`). A streaming endpoint that only
  *reads* the session for a cookieless client therefore orphans one on every request, and no data is
  lost.
- **The follow-up.** `IsModified` exists only on the internal `HttpSessionStoreSession`
  (`Http.Sessions/src/Internal/HttpSessionStoreSession.cs:55`), not on `IHttpStoredSession`, so a
  custom store cannot report it. Once Http.Sessions exposes it, add a throttled Warning
  `SessionStateLost` (id 2) and a `lost-sessions` counter for modified orphans only.
- **Never written:** the session id or cookie. The id is a bearer credential.

### 5.18 Assimalign.Cohesion.Web.Forms: `WebFormsEventSource`

The source lives in namespace `Assimalign.Cohesion.Web.Internal` and is named `Assimalign.Cohesion.Web.Forms`.

| Id | Name | Level | Payload | Raised from | Throttled |
|---|---|---|---|---|---|
| 1 | FormLimitExceeded | Verbose | limit (`HttpFormLimitExceededException.Message`), requestId | `src/Internal/FormsMiddleware.cs:58-62` | no |
| 2 | FormMalformed | Verbose | exceptionType, requestId | `FormsMiddleware.cs:63-66` | no |
| 3 | RejectionAborted | Warning | statusCode:int, suppressedCount:int | `FormsMiddleware.cs:84-88` | yes |

| Counter | Kind | Why it is exact |
|---|---|---|
| `form-limit-rejections` | PollingCounter, total | The first catch runs once per request. |
| `malformed-forms` | PollingCounter, total | The second catch runs once per request. |

Note: **[Certain]** all seven `HttpFormLimitExceededException` messages are built from configured limits
only:
- `HttpFormFeature.cs:167`;
- `BufferedReadStream.cs:235`;
- `HttpFormReader.cs:249` and `:326`;
- `HttpMultipartFormReader.cs:153` and `:221`;
- `HttpMultipartFormReaderStream.cs:133`.

Pin that with a test.

### 5.19 Assimalign.Cohesion.Web.WebSockets: `WebSocketEventSource`

| Id | Name | Level | Payload | Raised from | Throttled |
|---|---|---|---|---|---|
| 1 | HandshakeRejected | Verbose | reason (the handshake status name, OriginMultiple, OriginUnparseable or OriginNotAllowed), origin (normalized `scheme://host[:port]`, OriginNotAllowed only), routeTemplate, requestId | `src/Internal/WebSocketMiddleware.cs:35-39` and `:44-48`. `WebSocketPolicy.IsOriginAllowed` (`:53-76`) returns a reason | no |
| 2 | WebSocketOpened | Verbose | requestId, subProtocol | `src/Internal/WebSocketPolicyFeature.cs:51`, after a successful accept | no |
| 3 | WebSocketClosed | Verbose | requestId, closeStatus:int (-1 when none), closedByDrain:bool | `WebSocketPolicyFeature.cs:75`, `Release`, called from the `WebSocketMiddleware.cs:62-65` finally | no |
| 4 | DrainCloseFailed | Verbose | requestId, exceptionType, exceptionMessage | `src/Internal/DrainAwareWebSocket.cs:161-165` | no |

| Counter | Kind | Why it is exact |
|---|---|---|
| `current-websockets` | PollingCounter, gauge | +1 after accept, and -1 once in `Release` behind an `Interlocked.Exchange` flag. Tracked independently of the drain signal. |
| `total-websockets` | PollingCounter, total | Incremented once per single-shot accept. |
| `rejected-handshakes` | PollingCounter, total | One increment on either rejection branch. |

Note: Opened and Closed are Verbose because a socket's lifetime is per request. That keeps
Information-level forwarding quiet.

### 5.20 Assimalign.Cohesion.Web.Diagnostics: `HttpLoggingEventSource`

The HTTP log itself stays `ILogger`: it is the package's product. The source describes only the
package's own sink failures.

| Id | Name | Level | Payload | Raised from | Throttled |
|---|---|---|---|---|---|
| 1 | AccessLogFileOpened | Informational | path, sequence:int, existingBytes:long | `src/Internal/W3CAccessLogWriter.cs:175-178` | no |
| 2 | AccessLogWriteFailed | Error | operation (Write, Flush or Close), exceptionType, exceptionMessage, suppressedCount:int | a new catch-raise-rethrow in `Write` (`:60-97`; `CompositeLogger.cs:88-95` swallows the rethrow), the timer flush catch (`:145-149`) and the close flush catch (`:254-257`) | yes |
| 3 | AccessLogRetentionFailed | Warning | exceptionType, exceptionMessage, suppressedCount:int | `W3CAccessLogWriter.cs:229-232`, `:237-240` | yes |
| 4 | HttpLogEntryDropped | Warning | exceptionType, suppressedCount:int | `src/Internal/HttpLoggingMiddleware.cs:149-154` | yes |
| 5 | ClientAddressResolverFailed | Warning | exceptionType, suppressedCount:int | `HttpLoggingMiddleware.cs:411-414` | yes |

| Counter | Kind | Why it is exact |
|---|---|---|
| `failed-access-log-writes` | PollingCounter, total | Each `Write` call that throws is counted once. It counts calls, not entries. |
| `dropped-http-log-entries` | PollingCounter, total | The `Emit` catch at `:149` runs at most once per exchange. |

Notes:

- **[Certain] Why the counter was renamed.** The survey's `dropped-access-log-entries` was not exact.
  With a flush interval, `Write` only buffers (`:88-95`). A disk-full failure surfaces in the timer flush
  or the swallowed close flush, which lose every buffered entry, while the counter would count only
  `Write` calls that threw.
- **[Likely] Why flush failures are Error.** `StreamWriter.Flush` resets its character position before
  writing to the stream, so a failed flush loses the buffered entries, whatever the comment at `:147`
  says. They are Error with `operation`. Retention failures lose nothing and stay Warning.
- **Lock and loop safety.**
  - Raise events after leaving the writer's lock, because listeners run synchronously.
  - Pin `W3CAccessLogProvider`'s exchange-only filter (`:113-118`) with a test, so forwarded events
    cannot loop into a failing writer.
- **Prefix overlap.** Document it (§3 (d)).
- **Ordering.** Sequence this after Stage 13's #1297 and #1325.

### 5.21 Assimalign.Cohesion.Web.Health: `HealthCheckEventSource`

It covers Hosting.Health contributors too, which Web.Hosting.Health adapts onto this service.

| Id | Name | Level | Payload | Raised from | Throttled |
|---|---|---|---|---|---|
| 1 | HealthCheckWorsened | Warning | checkName, previousStatus (Unknown, Healthy or Degraded), status, cause (Reported, Exception or Timeout), exceptionType, exceptionMessage | new per-registration last-status slots, an `int[]` parallel to `_registrations` updated with `Interlocked.Exchange`, in `src/Internal/HealthCheckService.cs:49` | no |
| 2 | HealthCheckImproved | Informational | checkName, previousStatus, status | the same slots, when the status improves | no |
| 3 | HealthCheckFailed | Verbose | checkName, failureStatus, cause (Exception or Timeout), exceptionType, exceptionMessage, timeoutMilliseconds:double | `HealthCheckService.cs:85-96` (timeout) and `:97-106` (exception) | no |

| Counter | Kind | Why it is exact |
|---|---|---|
| `failed-checks` | PollingCounter, total | Each check run reaches at most one of the two catches. |

Notes:

- **Seeding.** Slots start at an Unknown sentinel (-1).
  - A first observation of Healthy raises nothing.
  - A first observation of Degraded or Unhealthy raises event 1 with `previousStatus=Unknown`, a state
    that was actually observed.
- **Per-instance tracking.** Transitions are tracked per health-service instance. `HealthChecksBuilder.Build`
  (`:33`) creates independent slots, and Database's admin plane builds its own service, so one check
  registered in two services raises two transitions. Document it.
- **Why transitions.** They catch a check that returns Unhealthy without throwing, which per-probe
  failure events miss.
- **Exception text.** Dependency exception messages can carry host, database or user names. Rule 11
  allows them in events, and DESIGN says so. The HTTP response is another matter (§11, defect 3).

### 5.22 Totals

- **Sources:** 21 of 37 Web assemblies.
- **Events:** 63.

  | Level | Events |
  |---|---|
  | Critical | 1 |
  | Error | 8 |
  | Warning | 21 |
  | Informational | 7 |
  | Verbose | 26 |

- **Counters:** 35. **Keywords:** none.
- **Throttled:** 17 events, and none latched.

  | Source | Throttled events |
  |---|---|
  | Hosting | 10, 15 |
  | Hosting.Resources | 1, 2 |
  | Antiforgery | 2 |
  | SecurityHeaders | 1 |
  | RateLimiting | 2 |
  | RequestTimeouts | 1 |
  | Caching | 2 |
  | Compression | 2, 3 |
  | StaticFiles | 4 |
  | Forms | 3 |
  | Diagnostics | 2, 3, 4, 5 |

- **Against the synthesized scope**, which had 62 events and Critical 2, Error 6, Warning 24,
  Informational 7, Verbose 23:
  - ErrorHandling gains `ResponseAbortedByPeer`.
  - `AcceptLoopFaulted` moves from Critical to Error.
  - `ResponseHeadRefused` moves from Warning to Error.
  - `ConnectionCapReached` and the Sessions event move from Warning to Verbose.
- **Setup:** 9 test-only `InternalsVisibleTo` grants and 14 new Web.Server references.

## 6. No source, and why

| Assembly | Why it has no source |
|---|---|
| `Assimalign.Cohesion.Web` | The area root: contracts and pure `Use`/`UseWhen`/`Run` helpers, with no runtime state, loop or swallowed failure. `Web.Hosting/docs/DESIGN.md:557-560` already declined this name, because it would claim signals for every Web package. |
| `Web.Server` | Contracts only. Web.Hosting implements and reports them, and #1300 owns the client-fault latch. |
| `Web.Hosting.Health` | A stateless adapter. Its one runtime failure (`HealthChecksBuilderExtensions.cs:82`) is thrown, and Web.Health reports contributor faults and timeouts. |
| `Web.Testing` | A shipped test harness, not a runtime component (the Connections.InMemory precedent). It never gets `InternalsVisibleTo` into a source (rule 3). |
| `Web.ApplicationModel` | A plan-time declarative plane. Invalid plans throw, and control-plane outcomes belong to Hosting.Resources. |
| `Web.Routing` | Failures throw at startup or reach the exception boundary. A 404 or 405 is a status, and the matched template is already the span's `http.route`. |
| `Web.Api` | Binding failures are client-driven 400s, already visible in the access log and in `http.server.request.duration` by status and route. A source is possible: generated thunks could call a public Web.Api helper that performs the rejection and raises the event. That is an operation, not a rule-2 forwarder. The signal does not justify new public API. |
| `Web.OpenApi` | Build failures reach the boundary and are not cached. First-route-wins is documented behavior, and the package is development-facing. |
| `Web.Validation` | A 400 is a client outcome by design, and validation errors echo user input. |
| `Web.Serialization` | A registry with call-site extensions. Faults throw, negotiation returns false for the caller's 406, and the package has no lifecycle. |
| `Web.Query` | Every outcome (400, 415, 406, 304, 412) is a visible client-driven protocol status, and nothing is swallowed. |
| `Web.ProblemDetails` | A payload library with no lifecycle. Its deterministic degradation serves last-chance error handling. |
| `Web.Authentication` | The root dispatcher. Handlers own every failure decision and get their own sources (rule 2). Counts at this level would not be exact, because Authorization re-enters per scheme. |
| `Web.Rewrite` | A loop or a regex timeout throws to the boundary, and redirects are configured answers. `DESIGN.md:308-317` deliberately emits nothing. |
| `Web.HttpsPolicy` | 307 and 308 rates are visible by route in `http.server.request.duration`. No source would see the redirect-loop root cause reliably. The most common loop is a TLS-terminating proxy with no `UseForwardedHeaders` at all, and then nothing raises anything. ForwardedHeaders' `ForwardingStopped(UntrustedPeer)` covers only the case where the middleware is present. **[Certain]** The DESIGN already describes the loop ("Behind a TLS-terminating proxy") and the HSTS skip on committed heads ("Emission point"). What it lacks is how to recognize the loop when nothing reports it (§11, item 5). Stage 12's #1324 also edits the package. |
| `Web.CookiePolicy` | The application already observes drops through `OnRejected`. A consent drop is the policy working, and a repair is the default `Upgrade` action doing its job. Revisit if support cases show silently dropped cookies. |
| `Web.Refs` / `Web.Runtime` | Framework producer shells that compile nothing. |

## 7. Relation to existing telemetry

- **#1064 (server traces and metrics).** Web.Hosting's `ActivitySource` and `Meter` keep spans,
  `http.server.request.duration` and `http.server.active_requests`.
  - No event source in this scope counts requests or raises per-request Start/Stop events.
  - Every counter here attributes something the Meter cannot: which middleware rejected a request and
    why, or a failure nothing measures.
  - This epic adds no Meter instruments. OTel-convention per-feature instruments stay with #317, which
    must say which instrument is authoritative.
  - The forwarder does not forward counters.
- **#147 (Web.Hosting diagnostics).** It is migrated into `WebServerEventSource` (§3 (a), §5.1). The issue
  stays open until T1 lands, and T1 closes it.
- **#1300 (transport rejections, Stage 14).** It owns the following:
  - requests the transport answers itself (400, 408, 413, 414, 431);
  - HTTP/2 and HTTP/3 protocol errors;
  - the #1340/#1378 client-fault latch.

  No Web source reports any of them. The asymmetry with decision 31, where #1300 gets both a Meter and
  an EventSource, holds because transport rejections never reach the server span, while Web rejections
  do. T0 adds three things to #1300's acceptance:
  - the rate-enforcement 408s from #1085 and #1383;
  - #1384's slow-reader response-rate aborts;
  - its exact source name, which joins the default forwarder's prefix list (§3 (a)).
- **#1384 and #1378.** T0 adds to their acceptance that a response-side client fault is latched where
  the exception boundary can read it. That retires §5.3's type check. **[Likely]** If #1384 has landed
  by T0, file the latch as a follow-up in #1300's lane instead.
- **#1297 (HTTP logging trace ids).** It makes the HTTP log record `IWebRequestIdFeature`'s id, the same
  `requestId` every per-request event carries. Sources sequence after it.
- **Follow-ups outside Web**, each filed in its own program:
  - **Security.DataProtection:** a source for unknown or revoked keys and an unreadable repository
    (`KeyRing.cs:130-190`).
  - **Hosting.Resources:** a source for command outcomes (501, 409, applied).
  - **Http.Forms:** a limit-name property on `HttpFormLimitExceededException`.
  - **Http.Sessions:** store load and commit failures, and `IsModified` on `IHttpStoredSession`.

## 8. Test strategy

- **Four required tests per source.** Each goes in `<Subject>EventSourceTests` in the source's own test
  project, in a `[CollectionDefinition(..., DisableParallelization = true)]` collection:
  1. the name;
  2. the strict manifest, with IL2026 and IL2111 suppressed under a test-only justification;
  3. lifecycle under an `EventListener`, with each event exactly once, its declared payload names, no
     event 0, and gauges back to their start;
  4. counters, with a short `EventCounterIntervalSec`.
- **Nine test-only `InternalsVisibleTo` grants [Certain]:** Hosting.Resources, ErrorHandling,
  Authentication.Bearer, Authentication.Cookie, Authorization, Antiforgery, HostFiltering,
  RequestTimeouts and Forms. The other 12 already grant their tests. Never grant Web.Testing or any
  shipped assembly.
- **Assert by delta and identifier.** Never assert an absolute counter value. Counters are process-wide,
  and earlier tests' background work, such as Web.Testing's detached cleanup, can still raise events.
  Identifiers: `serverId`, `connectionId`, `requestId`, `resourceName`, `scheme`, `checkName`.
- **Doubles.** Use existing doubles where they exist (ErrorHandling's `TestHttpContext`), and Web.Testing
  where the real host is needed (Authorization, HostFiltering).
- **One recorder [Certain].** `EventSourceRecorder` is already copied into 6 test projects. T0 picks one
  canonical test-only file, linked into each test project and named in `testing.md`, instead of 21 more
  copies. Rule 3's provider concern does not apply: a recorder is an `EventListener`, not a source, and
  test assemblies do not ship.
- **Acceptance tests the corrections require:**
  - an HTTP/1.1 slow reader on a streamed endpoint raises no ErrorHandling Error event and no
    `total-faults` increment;
  - a default entry-point Web application forwards no `Connections.*` or DependencyInjection entries;
  - a nested `CreateBuilder()` registers no forwarder;
  - an application's own `AddEventSourceForwarding` wins without `Build` throwing;
  - a handler `ArgumentException` reports `phase=Handler`;
  - a read-only session access for a cookieless client after the head commits raises Verbose only;
  - a first unhealthy probe reports `previousStatus=Unknown`;
  - the Sdk.Web evaluation test.
- **Conformance roll-up (T11).** One test over every App.Web member assembly asserts:
  - at most one `EventSource` subclass per assembly;
  - `internal sealed`, in namespace `{RootNamespace}.Internal`;
  - the name equal to the assembly name;
  - a strict manifest;
  - none in an assembly on the not-instrumented list.

## 9. Work breakdown

One feature (the epic, #1396, `L03.01.01.38`) and twelve tasks (#1397–#1408). Wave 1 tasks run in
parallel. Only `docs/EVENT_SOURCES.md` rows collide, and each task appends its own.

| Task | Issue | Scope | Size | Blocked by |
|---|---|---|---|---|
| T0 | #1397 | Decisions (a), (d) and (f); the event-source.md amendment; the canonical recorder; #1300, #1384 and #1378 acceptance; filing the §7 follow-ups | S | owner review, at the Stage 12 review |
| T1 | #1398 | Web.Hosting `WebServerEventSource`, replacing #147 | L | T0 (a); #1079 → #1078, #1156 and #1388 (Stage 13); #1389 (Stage 14) |
| T2 | #1399 | Default forwarding and NativeAOT delivery | M | T1; T0 (a) and (f); #1300's source name |
| T3 | #1400 | Web.Hosting.Resources | M | T0 |
| T4 | #1401 | Web.ErrorHandling | M | T0; #1388 |
| T5 | #1402 | Edge group: Cors, SecurityHeaders, HostFiltering and ForwardedHeaders | M | T0; #1156 (SecurityHeaders and Cors) |
| T6 | #1403 | Authentication group: Bearer, Cookie, Authorization and Antiforgery | M | T0 |
| T7 | #1404 | Traffic group: RateLimiting, RequestTimeouts, Compression and Forms | M | T0 |
| T8 | #1405 | Content group: Caching, StaticFiles, Sessions and WebSockets | M | T0; #1324 (StaticFiles) |
| T9 | #1406 | Web.Health | M | T0 |
| T10 | #1407 | Web.Diagnostics | M | T0; #1297 → #1325 |
| T11 | #1408 | Conformance test and documentation roll-up | S | T1-T10 |

### Every source task also meets these criteria

- **Shape.** The source is named exactly for its assembly and lives in `src/Internal/EventSource/` as
  `internal sealed`. It has the events and counters of §5 and no keywords.
- **Hygiene.** Every allocating argument is computed inside `IsEnabled`. Every throttled event carries
  `suppressedCount`, and its counter stays exact. No forbidden field appears (§3 (d)).
- **Tests.** The four required tests run in a serial collection, plus the acceptance tests §8 lists for
  that source.
- **Docs.** A **Diagnostics** section in the project's `DESIGN.md` and a row in `docs/EVENT_SOURCES.md`,
  in the same change.
- **AOT.** No trim or AOT analyzer warnings.

### Task acceptance

- **T0: decisions and rule amendment.**
  - (a), (d) and (f) are recorded as numbered decisions in `HTTP_WEB_PROGRAM_PLAN.md` §7.4.
  - `event-source.md` is amended with the §3 (d) clauses, with its `paths:` widened, and `checklist.md:43` is extended.
  - `docs/EVENT_SOURCES.md` lists the not-instrumented Web assemblies of §6, each with a one-line reason.
  - The canonical `EventSourceRecorder` exists and is named in `testing.md`.
  - The acceptance of #1300, #1384 and #1378 is extended (§7).
  - The follow-ups of §7 are filed. The §11 defects were filed with the epic (#1409–#1413).
- **T1: Web.Hosting.**
  - The §5.1 events and counters exist.
  - `WebApplicationServerLog` and `WebApplicationServerOptions.Logger` are deleted, and no `ILogger`
    self-description remains.
  - `serverId` is a process-unique sequence.
  - `connection-faults` counts a connection once across `:477` and `:507`.
  - The 8 diagnostics tests are rewritten against an `EventListener`.
  - DESIGN "Diagnostics (#147)" is rewritten, recording the category and attribute-key break.
  - T1 closes #147.
- **T2: forwarding and AOT.**
  - Under `_isEntryPoint` only, `Build` registers forwarding after `Sources.Clear()`, for
    `Assimalign.Cohesion.Web.` plus #1300's exact name, and only when no forwarder is registered. That
    check uses a new `ILoggerFactoryBuilder` probe.
  - An opt-out exists.
  - No default filter rule is added, and a test proves the HTTP access log at Information is unaffected.
  - `Logging.EventSource` is added to the framework closure (an App-kernel root, or App.Web plus the 17
    area lists). The closure tests and the Web.Runtime pack pass, and `docs/DEPENDENCIES.md` is
    regenerated.
  - Sdk.Web defaults `EventSourceSupport`, with the size delta recorded and the evaluation test present.
  - The AOT guard sets `EventSourceSupport=true`. Its published linux-x64 binary asserts, in process,
    that `ServerBound` and `ServerStopped` arrive with their payload names and that no event 0 does.
  - The Logging.EventSource DESIGN "Packaging" section is rewritten.
  - Coordinate with the uncommitted `AotGuard/Program.cs` change in this worktree.
- **T3: Hosting.Resources.**
  - `AuthorizeAsync`, `VerifyAsync` and `BootstrapTokenVerifier.Validate` return an internal reason code.
  - `CommandMalformed` carries `phase`.
  - Events 1 and 2 are throttled.
  - No credential, caller identity, command key or payload appears in any payload.
  - The source is documented as covering every area's control plane.
- **T4: ErrorHandling.**
  - Peer aborts raise Verbose `ResponseAbortedByPeer` and stay out of both fault counters, proven by the
    slow-reader test.
  - `SuppressDiagnosticsCallback` is evaluated once and routes the fault to event 2. Its XML doc says so.
  - `ErrorHandlerFailed` comes from an exception filter, and the handler's exception propagates unchanged.
  - The non-goal at `DESIGN.md:208-209` is replaced, and the Web.Hosting DESIGN pointer at `:760-763` is updated.
- **T5: Edge group.**
  - Cors: the evaluator returns a reason and allocates nothing on success.
  - SecurityHeaders excludes 304s, 101s, upgraded exchanges and `DisableSecurityHeaders` endpoints,
    with a WebSocket and extended-CONNECT test.
  - HostFiltering truncates the host to 253 characters.
  - ForwardedHeaders raises its event only when forwarding headers are present, and writes the peer IP
    only for UntrustedPeer at depth 0.
- **T6: Authentication group.**
  - Bearer splits NoMatchingKey from SignatureInvalid, and truncates `kid` and `alg` inside `IsEnabled`.
  - Authorization adds the internal requirement index without changing the public API.
  - Antiforgery's FormMalformed carries no exception text.
  - Every counter counts once per request and scheme, tested with Authorization re-authenticating.
- **T7: Traffic group.**
  - The RateLimiting partition key is never written.
  - RequestTimeouts raises on the request flow and records the armed interval.
  - Compression writes recognized codings only and counts in the middleware catch.
  - Forms pins the configured-only limit messages.
- **T8: Content group.**
  - The Caching hit and miss counters are benchmarked against a budget, and dropped if they exceed it.
  - StaticFiles' mount events fire once per pipeline build.
  - Sessions raises Verbose `SessionOrphaned`, with no Warning and no session id.
  - The WebSockets gauge returns to its start after the socket tests.
- **T9: Health.**
  - Slots are seeded Unknown, and transitions fire once under concurrent probes.
  - A check returning Unhealthy without throwing raises event 1 with `cause=Reported`.
  - Per-instance tracking is documented.
- **T10: Diagnostics.**
  - Write, flush and close failures raise `AccessLogWriteFailed`. `failed-access-log-writes` counts
    `Write` calls, tested with a failing stream.
  - Events are raised outside the writer's lock, and the exchange-only filter is pinned.
  - The prefix overlap is documented, and the non-goal at `DESIGN.md:246-247` is amended.
- **T11: roll-up.**
  - The conformance test of §8 exists.
  - `docs/EVENT_SOURCES.md` has 21 Web rows, and the Traces and metrics table links Web.Hosting's source.
  - `docs/resources/Web/DESIGN.md` gains one table mapping every Web signal to its owner: span, Meter,
    EventSource, or the transport. `resources/Web/README.md` links it.

## 10. Placement

**Recommendation.** Stage 14 ("Performance, operations and DX"), as a new lane G (diagnostics) beside
#1338 → #1300. T0 is taken at the Stage 12 review, where the owner already sets the v1 date, so Stage 14
sessions open with no gate.

**Why not earlier.** Stages 12 and 13 rewrite the code these sources instrument, so instrumenting first
means instrumenting twice.

| Item | Stage | What it changes that a source depends on | Task that waits |
|---|---|---|---|
| #1079 → #1078 | 13 | Host start failure and the Web.Hosting `OnStartingAsync` workaround; `Use` after build throws | T1 |
| #1156 | 13 | The response-starting hook, which changes how SecurityHeaders and Cors stage headers | T1, T5 |
| #1388 | 13 | What `HasStarted` reports after a refused head, which `ResponseHeadRefused`, `responseStarted` and SecurityHeaders' skip check read | T1, T4 |
| #1297 → #1325 | 13 | HTTP logging's trace id and body capture, in Web.Diagnostics | T10, and the `requestId` convention |
| #1324 | 12 | `Location` encoding in StaticFiles and HttpsPolicy | T8 |
| #1298 → #1299 | 12 | `WebExchangeTelemetry`, the site of event 15 | T1 |
| #1389 | 14 | The per-exchange response-interceptor path that Web.Hosting's client-fault interceptor uses **[Likely]** | T1, serial after it |
| #1300 | 14 | The transport source and its exact name | T2 only; the source tasks run in parallel with it |

**Inside Stage 14:**

- **Wave 1, in parallel:**
  - T1, serial after #1389;
  - T3, T4, T5, T6, T7, T8, T9 and T10.
- **Wave 2:**
  - T2, integrated together with T1 so visibility never regresses;
  - then T11.

That is about 10 to 12 agent sessions.

**Alternatives:**

1. **Settle #147's shape before v1.**
   - Run T0, T1 and T2 as one serial Web.Hosting session at the end of Stage 13, after #1079 → #1078,
     #1156 and #1388.
   - The other nine tasks stay in Stage 14.
   - T2 then ships the default forwarder before #1300's name exists, and the prefix list changes once
     more when #1300 lands.
2. **Stage 14 is already too heavy.**
   - Lane G becomes Stage 15, gated on "Stage 14 reviewed".
   - #1300 stays where decision 31 put it.

## 11. Side defects, filed separately

None of these is event-source work. Each has its own issue, filed under the Web area epic (#24).

| Issue | Defect | Where | Severity |
|---|---|---|---|
| #1410 | **[Certain]** `ResponseCompletionFeature.CompleteAsync` stops at the first callback that throws, and every later callback is skipped. The control plane's deferred stop is one of them whenever a callback registered ahead of it throws: the stop is acknowledged with 202 and never happens. **[Likely]** In-tree the control plane is the only registrant and runs first, so today that needs middleware composed ahead of it. The contract is public, so the defect stands regardless. | `Web.Hosting/src/Internal/ResponseCompletionFeature.cs:42-45`; the stop registration is at `Web.Hosting.Resources/src/ResourceControlPlaneMiddleware.cs:171`; the swallow is at `WebApplicationServer.cs:759-771` | Medium (Stage 13, Web correctness) |
| #1411 | **[Likely]** ErrorHandling's abort path returns normally after `CancelAsync`. The server then classifies the exchange as Completed, so completion callbacks can run after a reset and no `error.type` is recorded. | `Web.ErrorHandling/src/Internal/ExceptionBoundaryMiddleware.cs:107-110`; `WebApplicationServer.cs:614-616`, `:589-592` | Medium |
| #1409 | **[Certain] Security triage.** Web.Health's default JSON writer publishes exception messages to any prober, twice: `entry.Exception.Message` at `:78`, and the description, which `HealthCheckService.cs:101` sets to the same message. Dependency exceptions can carry host, database and user names, and readiness and liveness endpoints are typically unauthenticated. This is a larger exposure than any event payload (CWE-209). | `Web.Health/src/Internal/HealthCheckJsonResponseWriter.cs:78`, `:73`; `HealthCheckService.cs:101`; the default writer is set at `HealthEndpointOptions.cs:26` | High, pending triage |
| #1412 | **[Certain]** `VerifyAsync` builds a new `BootstrapTokenVerifier`, a JWK parse and an ES256 validator, on every Bearer request to the control plane. | `Web.Hosting.Resources/src/ResourceControlPlaneMiddleware.cs:494` | Low (Stage 14, performance) |
| #1413 | **Docs.** HttpsPolicy's DESIGN already explains the proxy redirect loop (`:55-95`) and the HSTS skip on committed heads (`:178-186`) **[Certain]**. It does not say that no event reports the loop when `UseForwardedHeaders` is missing, or how an operator recognizes it: a 307 or 308 rate on every route, plus `ForwardingStopped(UntrustedPeer)` when the middleware is present but the proxy is untrusted. | `Web.HttpsPolicy/docs/DESIGN.md`, "Behind a TLS-terminating proxy" | Low |

Also seen while verifying, and not filed here because the owner should confirm intent first:

- **[Likely] Standalone control planes are open by design.** With no gateway identity, `/cohesion/v1/stop`
  and `/cohesion/v1/commands` are unauthenticated on the shared port
  (`ResourceControlPlaneMiddleware.cs:76`; DESIGN: "A missing gateway identity enables standalone
  execution"). If standalone execution is meant only for development, that should be stated and
  enforced.
- **Whether `AcceptLoopFaulted` should stop the host** (§5.1).
