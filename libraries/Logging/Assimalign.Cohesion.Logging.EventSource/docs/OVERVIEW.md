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

```csharp
using ILoggerFactory loggerFactory = new LoggerFactoryBuilder()
    .AddProvider(new ConsoleLoggerProvider())
    .SetMinimumLevel(LogLevel.Information)
    .AddRule("Assimalign.Cohesion.Connections.Tcp", LogLevel.Debug)
    .Build();

// Every Cohesion event source, at the levels the factory accepts.
using IDisposable forwarding = loggerFactory.ForwardEventSources();

// Cohesion sources plus one of the runtime's own.
using IDisposable withTls = loggerFactory.ForwardEventSources(new EventSourceForwardingOptions
{
    Sources = { "System.Net.Security" },
});
```

In a hosted application, forward from the factory the host built, for the life of the application:

```csharp
WebApplicationBuilder builder = WebApplication.CreateBuilder(args);
builder.Logging.AddProvider(new ConsoleLoggerProvider());

WebApplication app = builder.Build();
using IDisposable forwarding = app.Context.ServiceProvider
    .GetRequiredService<ILoggerFactory>()
    .ForwardEventSources();
await app.RunAsync();
```

NativeAOT applications receive events only when published with
`<EventSourceSupport>true</EventSourceSupport>`.

## Key Types

- `EventSourceLoggerFactoryExtensions` — `ForwardEventSources(EventSourceForwardingOptions?)` on
  `ILoggerFactory`; returns the `IDisposable` that stops forwarding.
- `EventSourceForwardingOptions` — `Sources`, the name prefixes to forward, and the
  `CohesionSourcePrefix` constant.

## Source Layout

- `src/EventSourceForwardingOptions.cs` — the options shape.
- `src/Extensions/EventSourceLoggerFactoryExtensions.cs` — the public verb.
- `src/Internal/EventSourceLogForwarder.cs` — the `EventListener`: attach, enable, forward, contain.
- `src/Internal/EventSourceLogMapper.cs` — level mapping and event-to-entry translation.
- `src/Properties/AssemblyInfo.cs` — `InternalsVisibleTo` for tests.
