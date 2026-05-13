# Assimalign.Cohesion.Logging

## Summary

Cohesion logging foundation. Defines the structured log event model, logger and provider
contracts, factory composition (with category filtering and enrichment), and the scope
lifecycle. Concrete sinks live in sibling packages.

## Status

- Status: Stable foundation.
- Production source files: 15.
- Project references: `Assimalign.Cohesion.Core` (for the `LogId` value type generator
  pipeline).
- Package references: None.
- `NotImplementedException` markers: 0.

## Primary Responsibilities

- Define `ILogEntry` as the immutable structured log event consumed by every provider.
- Define `ILogger`, `IScopedLogger`, `ILoggerProvider`, `ILoggerFactory`, and the builder.
- Provide a thread-safe `LoggerFactory` that caches composite loggers per category, fans out
  to every provider, applies category-prefix filters, and runs registered enrichers.
- Document the scope and enrichment contracts so providers, hosts, and integration packages
  honor them consistently.

## Project Family

| Project | Purpose |
| --- | --- |
| `Assimalign.Cohesion.Logging` | Foundation contracts + factory composition. |
| `Assimalign.Cohesion.Logging.Console` | Console sink. |
| `Assimalign.Cohesion.Logging.Debug` | `System.Diagnostics.Debug` sink. |

Future providers (file rolling, structured collectors, OpenTelemetry bridge) live in their
own packages and depend on the foundation.

## Dependency Direction

- The foundation depends only on `Assimalign.Cohesion.Core`.
- Every provider depends on the foundation.
- The foundation never depends on a provider.

## Key Types

- `ILogEntry` / `LogEntry` / `LogEntryBuilder` - the event model.
- `LogLevel` - severity enum (`Trace`, `Debug`, `Information`, `Warning`, `Error`,
  `Critical`, `Event`, `None`).
- `LogId` - generated `Ulid` wrapper with `Empty` / `New()` factories.
- `ILogger` / `IScopedLogger` - writer contracts.
- `ILoggerProvider` - sink factory.
- `ILoggerFactory` / `LoggerFactory` - root cache + fan-out.
- `ILoggerFactoryBuilder` / `LoggerFactoryBuilder` - fluent registration.
- `LoggerFactoryOptions` - frozen registration snapshot.
- `ILogEnricher` - attribute pipeline.
- `LoggerExtensions` - typed helpers (`LogTrace`, `LogInformation`, `LogError`, ...).

## Source Layout

- `src/Abstractions/` - root contracts (`ILogger`, `ILogEntry`, `ILoggerFactory`,
  `IScopedLogger`, `ILoggerProvider`, `ILoggerFactoryBuilder`, `ILogEnricher`).
- `src/Extensions/LoggerExtensions.cs` - ergonomic helpers.
- `src/Internal/CompositeLogger.cs` - per-category fan-out.
- `src/Internal/ScopedCompositeLogger.cs` - scope lifecycle.
- `src/Internal/NoopScopedLogger.cs` - resilience helper.
- `src/LogEntry.cs`, `src/LogEntryBuilder.cs`, `src/LogLevel.cs` - event types.
- `src/LoggerFactory.cs`, `src/LoggerFactoryBuilder.cs`, `src/LoggerFactoryOptions.cs` -
  factory plumbing.
- `src/ValueTypes/LogId.cs` - LogId source-generation spec (the actual file is generated
  under `obj/CodeGeneration/`).
- `src/Properties/AssemblyInfo.cs` - `InternalsVisibleTo` for tests.
