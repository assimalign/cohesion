# Assimalign.Cohesion.Logging.EventSource

## Summary

Forwards `System.Diagnostics.Tracing` events into Cohesion logging. Cohesion libraries keep their event
sources internal (`.claude/rules/event-source.md`); this package is how an application sees those events
in its own log sinks — console, debug, OTLP, or any other `ILoggerProvider` — without referencing a single
event source type.

## Status

- Status: New (2026-09). First instrumented libraries: `Assimalign.Cohesion.Connections.Tcp`, `.Quic`,
  `.NamedPipes`, and `.Udp`.
- Project references: `Assimalign.Cohesion.Logging`.
- Package references: None.
- `NotImplementedException` markers: 0.

## Primary Responsibilities

- Enable every event source whose name starts with a configured prefix — `Assimalign.Cohesion.` by
  default — including sources created after forwarding starts.
- Enable each source only as verbosely as the target factory's loggers accept for that source's
  category, so a disabled level costs the instrumented library nothing.
- Turn each event into an `ILoggerEntry`: category = source name, message = the event's formatted
  message template, attributes = the payload plus `EventId`, `EventName`, and a non-empty `ActivityId`.
- Never let a logging failure reach the code that raised the event.

## Usage

Register forwarding on the builder, and the factory owns it: forwarding starts when the factory is built
and stops when it is disposed.

```csharp
using ILoggerFactory loggerFactory = new LoggerFactoryBuilder()
    .AddProvider(new ConsoleLoggerProvider())
    .SetMinimumLevel(LogLevel.Information)
    .AddRule("Assimalign.Cohesion.Connections.Tcp", LogLevel.Debug)
    .AddEventSourceForwarding()                       // every Cohesion event source
    .Build();
```

Name more sources by prefix; the Cohesion default stays unless you clear it. Register forwarding once —
the forwarder is listed as `EventSource` in `loggerFactory.Forwarders`, and a second registration fails
the build:

```csharp
builder.AddEventSourceForwarding(new EventSourceForwardingOptions
{
    Sources = { "System.Net.Security" },
});
```

In a hosted application, register it on the host's logging builder. The factory is built before the host
starts, so the events raised while listeners bind are forwarded too:

```csharp
WebApplicationBuilder builder = WebApplication.CreateBuilder(args);
builder.Logging
    .AddProvider(new ConsoleLoggerProvider())
    .AddEventSourceForwarding();

await builder.Build().RunAsync();
```

To forward into a factory you did not build, or for a span shorter than the factory's life, call
`ForwardEventSources()` on the factory and dispose the forwarder it returns before the factory:

```csharp
using ILoggerForwarder forwarding = loggerFactory.ForwardEventSources();
```

NativeAOT applications receive events only when published with
`<EventSourceSupport>true</EventSourceSupport>`.

## Key Types

- `EventSourceLoggerFactoryBuilderExtensions` — `AddEventSourceForwarding(EventSourceForwardingOptions?)`
  on `ILoggerFactoryBuilder`; the factory owns the forwarder.
- `EventSourceLoggerFactoryExtensions` — `ForwardEventSources(EventSourceForwardingOptions?)` on
  `ILoggerFactory`; returns the `ILoggerForwarder`, which the caller disposes.
- `EventSourceForwardingOptions` — `Sources`, the name prefixes to forward, and the
  `CohesionSourcePrefix` constant.

## Source Layout

- `src/EventSourceForwardingOptions.cs` — the options shape.
- `src/Extensions/EventSourceLoggerFactoryBuilderExtensions.cs` — the builder verb.
- `src/Extensions/EventSourceLoggerFactoryExtensions.cs` — the factory verb.
- `src/Internal/EventSourceLogForwarder.cs` — the `EventListener` and `ILoggerForwarder`: validate options,
  attach, enable, forward, contain.
- `src/Internal/EventSourceLogMapper.cs` — level mapping and event-to-entry translation.
- `src/Properties/AssemblyInfo.cs` — `InternalsVisibleTo` for tests.
