# Assimalign.Cohesion.Logging Design

## Design Intent

The foundation package is a contract-and-composition layer, not a transport. It defines what a
log event looks like, how loggers are created and cached, how scopes nest, and how enrichers
contribute attributes; transports (console, debug, file, structured collectors) live in
sibling packages.

## Architecture

```text
caller -> ILogger.Log(ILogEntry)
                                 |
                                 v
                  CompositeLogger (per category, cached by LoggerFactory)
                    |   |   |
                    v   v   v
        ILogger (provider 1)   ...   ILogger (provider N)
```

`CompositeLogger` is the only logger callers normally see. It owns:

1. The per-category minimum-level decision (defaults to `LoggerFactoryOptions.MinimumLevel`
   with category-prefix overrides from `Filters`).
2. The enrichment pipeline (runs in registration order; enrichers may add new attribute keys
   but cannot overwrite caller-supplied attributes).
3. Fan-out to every registered provider, with per-provider failure isolation: an exception
   from one sink never aborts fan-out to the others.

## Log Entry Contract

`ILogEntry` is immutable and carries:

| Property | Type | Purpose |
| --- | --- | --- |
| `Id` | `LogId` | Unique entry id, generated as a `Ulid`. |
| `ParentId` | `LogId?` | Optional parent (scoped logger). |
| `Timestamp` | `DateTimeOffset` | UTC wall-clock at construction. |
| `Level` | `LogLevel` | Severity. |
| `Category` | `string` | Logger category (typically a fully qualified type name). |
| `Message` | `string` | Human-readable message; never null, may be empty. |
| `Exception` | `Exception?` | Optional captured exception. |
| `Attributes` | `IReadOnlyDictionary<string, object?>` | Structured key-value payload. |

Build entries through `LogEntryBuilder` for full control or through `LoggerExtensions`
helpers (`LogTrace`, `LogInformation`, `LogError`, ...) for the common shapes.

## Levels and Filtering

`LogLevel` ordering: `Trace < Debug < Information < Warning < Error < Critical < Event`,
plus `None` (which always returns `false` from `IsEnabled`).

Filtering is configured at build time:

- `SetMinimumLevel` sets the factory default (defaults to `Information`).
- `AddFilter("App.Network", LogLevel.Debug)` overrides for any category that starts with
  `App.Network` (case-insensitive). The most specific (longest) prefix wins.

## Factory Lifecycle

`LoggerFactory`:

1. Caches composite loggers per category (`ConcurrentDictionary` keyed
   case-insensitively).
2. Owns its providers: disposing the factory disposes every provider it was built with.
3. After `Dispose`, every other operation throws `ObjectDisposedException`.

`LoggerFactoryBuilder` is single-use: once `Build` has been called the builder is locked.
Duplicate provider names are rejected.

## Scopes

`ILogger.BeginScope(seed)` returns an `IScopedLogger`. Entries written through the scope
inherit `seed.Id` as their `ParentId` unless the entry already carries a `ParentId`. Scopes
nest: opening a scope on a scoped logger uses the inner scope's seed as the parent of any
further nested scopes. Scopes are disposable; double-dispose is a no-op.

If a provider throws while opening a scope, the composite substitutes a `NoopScopedLogger`
so the composite stays stable.

## Enrichment

`ILogEnricher.Enrich(entry, attributes)` runs once per entry per fan-out. The mutable
dictionary handed to the enricher prevents overwrites: if the entry already carries a key,
the enricher's add is dropped. Enricher exceptions are swallowed and the entry still ships.

## Transactional Logging - De-scoped

Earlier scaffolding defined an `ITransactionLogger` contract intended for buffered "commit"
flows. The contract was never implemented and there is no Cohesion consumer that requires
transactional log semantics today. The contract has been removed for this iteration.

If a transactional log surface becomes necessary (for example, to coalesce a burst of
diagnostics into a single audit event), it should be reintroduced as a separate package
(`Assimalign.Cohesion.Logging.Transactional` or similar) so the foundation stays narrow.

## Concurrency

- The factory cache uses `ConcurrentDictionary` for lock-free read paths.
- `LoggerFactoryBuilder` is **not** thread-safe; configure it on a single thread, then
  publish the resulting factory.
- Providers must be thread-safe. The console provider serializes writes through an internal
  lock so concurrent log calls do not interleave output.

## AOT and Trimming

- No reflection.
- No emit / dynamic codegen.
- `Dictionary<string, object?>` for attribute payloads is AOT-safe.
- The `LogId` source generator runs at build time and emits regular C# - it does not require
  runtime trimming roots.

## Layout

```text
Assimalign.Cohesion.Logging/
  src/
    Assimalign.Cohesion.Logging.csproj
    Abstractions/
      ILogEntry.cs
      ILogger.cs
      IScopedLogger.cs
      ILoggerProvider.cs
      ILoggerFactory.cs
      ILoggerFactoryBuilder.cs
      ILogEnricher.cs
    Extensions/
      LoggerExtensions.cs
    Internal/
      CompositeLogger.cs
      ScopedCompositeLogger.cs
      NoopScopedLogger.cs
    LogEntry.cs
    LogEntryBuilder.cs
    LogLevel.cs
    LoggerFactory.cs
    LoggerFactoryBuilder.cs
    LoggerFactoryOptions.cs
    ValueTypes/
      LogId.cs           (source-generator spec)
    Properties/
      AssemblyInfo.cs
  tests/
  docs/
    OVERVIEW.md
    DESIGN.md
    Assembly/
```

## Example: minimal setup

```csharp
ILoggerFactory factory = new LoggerFactoryBuilder()
    .AddProvider(new ConsoleLoggerProvider())
    .AddProvider(new DebugLoggerProvider())
    .SetMinimumLevel(LogLevel.Information)
    .AddFilter("App.Network", LogLevel.Debug)
    .AddEnricher(new ProcessEnricher())
    .Build();

ILogger logger = factory.Create("App.Network.Http");
logger.LogInformation("App.Network.Http", "Request {Method} {Url}",
    attributes: new Dictionary<string, object?> { ["Method"] = "GET", ["Url"] = url });

using IScopedLogger scope = logger.BeginScope("App.Network.Http", "request scope");
scope.LogDebug("App.Network.Http", "response {Status}",
    attributes: new Dictionary<string, object?> { ["Status"] = 200 });
```
