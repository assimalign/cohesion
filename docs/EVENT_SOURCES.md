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
| `Assimalign.Cohesion.Connections.Tcp` | Listener bound/closed; connection opened/closed; peer end-of-stream, back-pressure pause/resume, reset (Verbose); connection error | `current-connections`, `total-connections`, `connections-per-second` | [Tcp DESIGN.md](../libraries/Connections/Assimalign.Cohesion.Connections.Tcp/docs/DESIGN.md#diagnostics) |
| `Assimalign.Cohesion.Connections.Quic` | Listener bound/closed; connection opened/closed; stream opened/closed (Verbose); inbound handshake failed and dropped | `current-connections`, `total-connections`, `connections-per-second`, `current-streams`, `streams-per-second` | [Quic DESIGN.md](../libraries/Connections/Assimalign.Cohesion.Connections.Quic/docs/DESIGN.md#diagnostics) |
| `Assimalign.Cohesion.Connections.NamedPipes` | Listener bound/closed; connection opened/closed | `current-connections`, `total-connections`, `connections-per-second` | [NamedPipes DESIGN.md](../libraries/Connections/Assimalign.Cohesion.Connections.NamedPipes/docs/DESIGN.md#diagnostics) |
| `Assimalign.Cohesion.Connections.Udp` | Datagram connection opened (bind or connect)/closed | `current-connections`, `total-connections` | [Udp DESIGN.md](../libraries/Connections/Assimalign.Cohesion.Connections.Udp/docs/DESIGN.md#diagnostics) |
| `Assimalign.Cohesion.DependencyInjection` | Provider built; call site built, service resolved, scope disposed, provider descriptors, resolver compiled (Verbose); resolver compilation failed | none | [DependencyInjection DESIGN.md](../libraries/DependencyInjection/Assimalign.Cohesion.DependencyInjection/docs/DESIGN.md#diagnostics) |

Deliberately not instrumented: `Connections.InMemory` (a test driver), `Connections.Security` (TLS
handshakes are already reported by the runtime's `System.Net.Security` source, and a handshake that
fails on a TLS-layered listener is reported by `Assimalign.Cohesion.Connections`, which owns the
listener that closes the connection), and
`Http.Connections` (requests are traced and measured by the Web server below, and connections are
counted by the drivers above; its empty placeholder source was deleted, see its
[DESIGN.md](../libraries/Http/Assimalign.Cohesion.Http.Connections/docs/DESIGN.md#diagnostics)).

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
