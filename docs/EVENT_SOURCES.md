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
| `Assimalign.Cohesion.Connections.Tcp` | Listener bound/closed; connection opened/closed; peer end-of-stream, back-pressure pause/resume, reset (Verbose); connection error | `current-connections`, `total-connections`, `connections-per-second` | [Tcp DESIGN.md](../libraries/Connections/Assimalign.Cohesion.Connections.Tcp/docs/DESIGN.md#diagnostics) |
| `Assimalign.Cohesion.Connections.Quic` | Listener bound/closed; connection opened/closed; stream opened/closed (Verbose) | `current-connections`, `total-connections`, `connections-per-second`, `current-streams`, `streams-per-second` | [Quic DESIGN.md](../libraries/Connections/Assimalign.Cohesion.Connections.Quic/docs/DESIGN.md#diagnostics) |
| `Assimalign.Cohesion.Connections.NamedPipes` | Listener bound/closed; connection opened/closed | `current-connections`, `total-connections`, `connections-per-second` | [NamedPipes DESIGN.md](../libraries/Connections/Assimalign.Cohesion.Connections.NamedPipes/docs/DESIGN.md#diagnostics) |
| `Assimalign.Cohesion.Connections.Udp` | Datagram connection opened (bind or connect)/closed | `current-connections`, `total-connections` | [Udp DESIGN.md](../libraries/Connections/Assimalign.Cohesion.Connections.Udp/docs/DESIGN.md#diagnostics) |
| `Assimalign.Cohesion.Database` | Engine created, composed, dispose start/stop; engine dispose failed (Error); engine worker failed (per database, or a whole pass; Warning), recovered, loop faulted (Error), give-up failed (Error), pass start/stop and unfinished work (Verbose, `Workers`); database created, opened, dropped, closed; database operation failed (Error); database taken offline after a worker's failures persisted (Error); server started, stopped; server start failed (Error); server session negotiated (Verbose, `Sessions`), authenticated; session opened/closed (Verbose, `Sessions`); statement start/stop (Verbose, `Statements`); slow statement (Warning; `SlowStatementThresholdMs` argument, default 1000); statement failed (Error); explicit transaction begun, committed, rolled back, aborted (Verbose, `Transactions`); commit failed (Error) | `current-sessions` | [Database DESIGN.md](../resources/Database/Assimalign.Cohesion.Database/docs/DESIGN.md#diagnostics) |
| `Assimalign.Cohesion.Database.Hosting` | Offline database found (Warning); reopen attempted, succeeded; reopen failed (Warning); reopen abandoned; reopen loop failed (Error) | none | [Database.Hosting DESIGN.md](../resources/Database/Assimalign.Cohesion.Database.Hosting/docs/DESIGN.md#diagnostics) |
| `Assimalign.Cohesion.DependencyInjection` | Provider built; call site built, service resolved, scope disposed, provider descriptors, resolver compiled (Verbose); resolver compilation failed | none | [DependencyInjection DESIGN.md](../libraries/DependencyInjection/Assimalign.Cohesion.DependencyInjection/docs/DESIGN.md#diagnostics) |

Deliberately not instrumented: `Assimalign.Cohesion.Connections` (contracts; it performs no network
operations of its own), `Connections.InMemory` (a test driver), and `Connections.Security` (TLS
handshakes are already reported by the runtime's `System.Net.Security` source).

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
node no application tool attaches to). `Database.Sql.Schema` is expected to need none; that is
decided after the schema-provisioning redesign lands.

## Not yet conforming

These sources predate the convention. Their names will change when they are brought into line, so do
not build tooling against them.

| Assembly | Current name | Problem | Tracked by |
| --- | --- | --- | --- |
| `Assimalign.Cohesion.Resilience` | `AssimalignCohesionResilience` | Concatenated name; public constructor; no events. | #1038 |
| `Assimalign.Cohesion.Resilience.Retry` | *(empty)* | Empty name; an event method that writes nothing. | #1038 |
| `Assimalign.Cohesion.Resilience.Timeout` | `Assimalign.Cohesion.Resilience.TimeoutResilienceEventSource` | Type name in the source name; no events. | #1038 |
| `Assimalign.Cohesion.Http.Connections` | `Assimalign.Cohesion.Http.Connections` | Correct name, but an empty placeholder with no events. | #1039 |
