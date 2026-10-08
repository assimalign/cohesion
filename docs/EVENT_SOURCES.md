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
| `Assimalign.Cohesion.Database` | Engine worker failed (per database, or a whole pass; Warning); engine worker recovered; database taken offline after a worker's failures persisted (Error) | none | [Database DESIGN.md](../resources/Database/Assimalign.Cohesion.Database/docs/DESIGN.md#diagnostics) |
| `Assimalign.Cohesion.Database.Blob.Client` | Transfer start/stop per upload, download, delete, properties and listing (Verbose, `Transfers`); transfer failed (Error); listing cleanup failure the consumer never saw (Verbose) | none | [Database.Blob.Client DESIGN.md](../resources/Database/Assimalign.Cohesion.Database.Blob.Client/docs/DESIGN.md#diagnostics) |
| `Assimalign.Cohesion.Database.Client` | Connection opened, closed; failed to open (Error); broken (Warning); rented, returned (Verbose, `Pool`); exchange failed with a coded error (Verbose); failed download's rental not returned (Warning) | `current-connections`, `current-rented-connections`, `connections-opened-per-second`, `total-connection-failures` | [Database.Client DESIGN.md](../resources/Database/Assimalign.Cohesion.Database.Client/docs/DESIGN.md#diagnostics) |
| `Assimalign.Cohesion.Database.Graph.Client` | Query start/stop per query, execute and path query (Verbose, `Queries`); query failed (Error) | none | [Database.Graph.Client DESIGN.md](../resources/Database/Assimalign.Cohesion.Database.Graph.Client/docs/DESIGN.md#diagnostics) |
| `Assimalign.Cohesion.Database.Hosting` | Offline database found (Warning); reopen attempted, succeeded; reopen failed (Warning); reopen abandoned; reopen loop failed (Error) | none | [Database.Hosting DESIGN.md](../resources/Database/Assimalign.Cohesion.Database.Hosting/docs/DESIGN.md#diagnostics) |
| `Assimalign.Cohesion.Database.KeyValuePair.Client` | Command start/stop (Verbose, `Commands`); command failed (Error); observer hook failed (Warning); the SQL client's ids and payloads | none | [Database.KeyValuePair.Client DESIGN.md](../resources/Database/Assimalign.Cohesion.Database.KeyValuePair.Client/docs/DESIGN.md#diagnostics) |
| `Assimalign.Cohesion.Database.Protocol` | Frame read, frame written, once per wire frame (Verbose, `Frames`) | none | [Database.Protocol DESIGN.md](../resources/Database/Assimalign.Cohesion.Database.Protocol/docs/DESIGN.md#diagnostics) |
| `Assimalign.Cohesion.Database.Security` | Authentication succeeded, rejected (Verbose); authenticator failed (Error) | none | [Database.Security DESIGN.md](../resources/Database/Assimalign.Cohesion.Database.Security/docs/DESIGN.md#diagnostics) |
| `Assimalign.Cohesion.Database.Sql.Client` | Command start/stop (Verbose, `Commands`); command failed (Error); observer hook failed (Warning) | none | [Database.Sql.Client DESIGN.md](../resources/Database/Assimalign.Cohesion.Database.Sql.Client/docs/DESIGN.md#diagnostics) |
| `Assimalign.Cohesion.DependencyInjection` | Provider built; call site built, service resolved, scope disposed, provider descriptors, resolver compiled (Verbose); resolver compilation failed | none | [DependencyInjection DESIGN.md](../libraries/DependencyInjection/Assimalign.Cohesion.DependencyInjection/docs/DESIGN.md#diagnostics) |

Deliberately not instrumented: `Assimalign.Cohesion.Connections` (contracts; it performs no network
operations of its own), `Connections.InMemory` (a test driver), and `Connections.Security` (TLS
handshakes are already reported by the runtime's `System.Net.Security` source).

## Not yet conforming

These sources predate the convention. Their names will change when they are brought into line, so do
not build tooling against them.

| Assembly | Current name | Problem | Tracked by |
| --- | --- | --- | --- |
| `Assimalign.Cohesion.Resilience` | `AssimalignCohesionResilience` | Concatenated name; public constructor; no events. | #1038 |
| `Assimalign.Cohesion.Resilience.Retry` | *(empty)* | Empty name; an event method that writes nothing. | #1038 |
| `Assimalign.Cohesion.Resilience.Timeout` | `Assimalign.Cohesion.Resilience.TimeoutResilienceEventSource` | Type name in the source name; no events. | #1038 |
| `Assimalign.Cohesion.Http.Connections` | `Assimalign.Cohesion.Http.Connections` | Correct name, but an empty placeholder with no events. | #1039 |
