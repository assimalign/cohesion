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
    .Build();

using IDisposable forwarding = loggerFactory.ForwardEventSources();
```

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

Deliberately not instrumented: `Assimalign.Cohesion.Connections` (contracts; it performs no network
operations of its own), `Connections.InMemory` (a test driver), and `Connections.Security` (TLS
handshakes are already reported by the runtime's `System.Net.Security` source).

## Not yet conforming

These sources predate the convention. Their names will change when they are brought into line, so do
not build tooling against them.

| Assembly | Current name | Problem | Tracked by |
| --- | --- | --- | --- |
| `Assimalign.Cohesion.DependencyInjection` | `Assimalign-Cohesion-DependencyInjection` | Dash-separated, not the assembly name. | #1037 |
| `Assimalign.Cohesion.Resilience` | `AssimalignCohesionResilience` | Concatenated name; public constructor; no events. | #1038 |
| `Assimalign.Cohesion.Resilience.Retry` | *(empty)* | Empty name; an event method that writes nothing. | #1038 |
| `Assimalign.Cohesion.Resilience.Timeout` | `Assimalign.Cohesion.Resilience.TimeoutResilienceEventSource` | Type name in the source name; no events. | #1038 |
| `Assimalign.Cohesion.Http.Connections` | `Assimalign.Cohesion.Http.Connections` | Correct name, but an empty placeholder with no events. | #1039 |
