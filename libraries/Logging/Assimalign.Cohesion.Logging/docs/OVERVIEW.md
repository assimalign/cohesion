# Assimalign.Cohesion.Logging

## Summary

Cohesion logging foundation. Defines the structured log event model, logger and provider
contracts, factory composition (with a per-provider rule-based filter pipeline and enrichment),
and the scope lifecycle. Concrete sinks live in sibling packages.

## Status

- Status: Stable foundation.
- Production source files: 19.
- Project references: `Assimalign.Cohesion.Core` (for the `LogId` value type generator
  pipeline).
- Package references: None.
- `NotImplementedException` markers: 0.

## Primary Responsibilities

- Define `ILoggerEntry` as the immutable structured log event consumed by every provider.
- Define `ILogger`, `IScopedLogger`, `ILoggerProvider`, `ILoggerFactory`, and the builder.
- Provide a thread-safe `LoggerFactory` that caches composite loggers per category. The
  factory pre-resolves a single winning `LoggerFilterRule` per (provider, category) pair at
  composite-construction time, then gates entries per provider on level + optional
  `ILoggerFilter`.
- Document the scope, filter, and enrichment contracts so providers, hosts, and integration
  packages honor them consistently.

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

- `ILoggerEntry` / `LoggerEntry` / `LoggerEntryBuilder` - the event model.
- `LogLevel` - severity enum (`Trace`, `Debug`, `Information`, `Warning`, `Error`,
  `Critical`, `Event`, `None`).
- `LogId` - generated `Ulid` wrapper with `Empty` / `New()` factories.
- `ILogger` / `IScopedLogger` - writer contracts.
- `Logger` / `ScopedLogger` - abstract base classes that implement the boilerplate with
  non-virtual hot paths so derived sinks pay one virtual dispatch instead of two.
- `ILoggerProvider` / `LoggerProvider` - sink factory contract + abstract base class.
- `ILoggerFactory` / `LoggerFactory` - root cache + fan-out with per-provider rule gating.
  `LoggerFactory.Create(string)` returns the concrete `Logger` via covariant return.
- `ILoggerFactoryBuilder` / `LoggerFactoryBuilder` - fluent registration.
- `LoggerFactoryOptions` - mutable configuration shape consumed by the factory.
- `ILoggerEnricher` - attribute pipeline.
- `LoggerFilterRule` - one rule (provider type + category + level + custom filter, all
  optional). `ILoggerFilter` is the custom filter shape.
- `LoggerExtensions` - typed helpers (`LogTrace`, `LogInformation`, `LogError`, ...).

## Source Layout

- `src/Abstractions/` - root contracts (`ILogger`, `ILogger.Scoped.cs` (`IScopedLogger`),
  `ILoggerEntry`, `ILoggerFactory`, `ILoggerProvider`, `ILoggerFactoryBuilder`,
  `ILoggerEnricher`, `ILoggerFilter`).
- `src/Extensions/LoggerExtensions.cs` - ergonomic helpers.
- `src/Internal/CompositeLogger.cs` - per-category fan-out with per-provider rule gating.
- `src/Internal/ScopedCompositeLogger.cs` - scope lifecycle.
- `src/Internal/NoopScopedLogger.cs` - resilience helper.
- `src/Internal/LoggerFilterRuleSelector.cs` - rule selection algorithm.
- `src/LoggerEntry.cs`, `src/LoggerEntryBuilder.cs`, `src/LogLevel.cs` - event types.
- `src/Logger.cs`, `src/ScopedLogger.cs`, `src/LoggerProvider.cs` - reusable abstract base
  classes implementing the boilerplate with non-virtual hot paths.
- `src/LoggerFactory.cs`, `src/LoggerFactoryBuilder.cs`, `src/LoggerFactoryOptions.cs` -
  factory plumbing.
- `src/LoggerFilterRule.cs` - filter rule type.
- `src/ValueTypes/LogId.cs` - LogId source-generation spec (the actual file is generated
  under `obj/CodeGeneration/`).
- `src/Properties/AssemblyInfo.cs` - `InternalsVisibleTo` for tests.
