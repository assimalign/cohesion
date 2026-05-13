# Assimalign.Cohesion.Logging Design

## Design Intent

The foundation package is a contract-and-composition layer, not a transport. It defines what a
log event looks like, how loggers are created and cached, how scopes nest, how enrichers
contribute attributes, and how an optional per-entry filter gates fan-out. Transports
(console, debug, file, structured collectors) live in sibling packages.

## Architecture

```text
caller -> ILogger.Log(ILoggerEntry)
                                   |
                                   v
                  CompositeLogger (per category, cached by LoggerFactory)
                  |   filter gate (ILoggerFilter, optional)
                  |   enrichment pipeline (ILoggerEnricher, ordered)
                  |   per-sink fan-out (failures isolated)
                    |   |   |
                    v   v   v
        ILogger (provider 1)   ...   ILogger (provider N)
```

`CompositeLogger` is the only logger callers normally see. It owns:

1. The factory minimum level check (`LoggerFactoryOptions.MinimumLevel`); entries below the
   factory floor never reach the filter.
2. The optional per-entry filter (`ILoggerFilter`) gate; the filter receives the full
   `ILoggerEntry` so it can branch on category, level, attributes, exception, or any
   combination of them. A throwing filter is treated as `true` (admit) so a bad filter never
   silently drops entries.
3. The enrichment pipeline; enrichers run in registration order. Enrichers may add new
   attribute keys but cannot overwrite caller-supplied attributes.
4. Fan-out to every registered provider, with per-provider failure isolation: an exception
   from one sink never aborts fan-out to the others.

## Log Entry Contract

`ILoggerEntry` is immutable and carries:

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

Build entries through `LoggerEntryBuilder` for full control or through `LoggerExtensions`
helpers (`LogTrace`, `LogInformation`, `LogError`, ...) for the common shapes.

### `LoggerEntry` is a class, not a struct

The entry crosses the `ILoggerEntry` interface boundary multiple times in the pipeline
(extension method -> composite logger -> enrichment view -> underlying providers). Each cast
through the interface would box a struct, allocating a fresh heap object per boundary -
strictly worse than the single class allocation the current design produces. The class shape
also keeps the entry copy-by-reference, which matters for the enrichment pipeline that
re-emits enriched copies.

## Levels and Filtering

`LogLevel` ordering: `Trace < Debug < Information < Warning < Error < Critical < Event`,
plus `None` (which always returns `false` from `IsEnabled`).

Filtering happens in two stages:

1. **Factory minimum** (`LoggerFactoryOptions.MinimumLevel`, default `Information`). Applied
   unconditionally before anything else; entries below the floor are dropped before the
   filter sees them.
2. **`ILoggerFilter`** (optional). Sees the full entry and decides per-entry. The fluent
   `AddFilter(prefix, level)` helper accumulates rules into a `CategoryLoggerFilter`. Custom
   logic plugs in through `UseFilter(myFilter)`. Both can coexist; the entry must pass both.

To express "category X logs at Trace while everything else logs at Warning", set
`MinimumLevel = Trace` and add a filter that requires `entry.Level >= Warning` unless the
category matches X.

## Factory Lifecycle

`LoggerFactory`:

1. Caches composite loggers per category (`ConcurrentDictionary` keyed
   case-insensitively).
2. Owns its providers: disposing the factory disposes every provider it was built with.
3. After `Dispose`, every other operation throws `ObjectDisposedException`.

`LoggerFactoryBuilder` is single-use: once `Build` has been called the builder is locked.
Duplicate provider names are rejected.

`LoggerFactoryOptions` exposes its provider and enricher lists as mutable `IList<>`
collections so callers may populate the options directly when bypassing the builder. The
factory snapshots both lists at construction time; mutating the options after the factory
has been constructed has no effect on already-cached loggers.

## Scopes

`ILogger.BeginScope(seed)` returns an `IScopedLogger`. Entries written through the scope
inherit `seed.Id` as their `ParentId` unless the entry already carries a `ParentId`. Scopes
nest: opening a scope on a scoped logger uses the inner scope's seed as the parent of any
further nested scopes. Scopes are disposable; double-dispose is a no-op.

If a provider throws while opening a scope, the composite substitutes a `NoopScopedLogger`
so the composite stays stable.

## Enrichment

`ILoggerEnricher.Enrich(entry, attributes)` runs once per entry per fan-out. The mutable
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
      ILoggerEntry.cs
      ILogger.cs
      ILogger.Scoped.cs
      ILoggerProvider.cs
      ILoggerFactory.cs
      ILoggerFactoryBuilder.cs
      ILoggerEnricher.cs
      ILoggerFilter.cs
    Extensions/
      LoggerExtensions.cs
    Filters/
      CategoryLoggerFilter.cs
    Internal/
      CompositeLogger.cs
      ScopedCompositeLogger.cs
      NoopScopedLogger.cs
    LoggerEntry.cs
    LoggerEntryBuilder.cs
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
    .AddFilter("App.Network", LogLevel.Debug) // Network category must be Debug+
    .AddEnricher(new ProcessEnricher())
    .Build();

ILogger logger = factory.Create("App.Network.Http");
logger.LogInformation("App.Network.Http", "Request {Method} {Url}",
    attributes: new Dictionary<string, object?> { ["Method"] = "GET", ["Url"] = url });

using IScopedLogger scope = logger.BeginScope("App.Network.Http", "request scope");
scope.LogDebug("App.Network.Http", "response {Status}",
    attributes: new Dictionary<string, object?> { ["Status"] = 200 });
```

## Example: custom filter

```csharp
internal sealed class AuditOnlyFilter : ILoggerFilter
{
    public bool ShouldLog(ILoggerEntry entry) => entry.Attributes.ContainsKey("audit");
}

ILoggerFactory auditOnly = new LoggerFactoryBuilder()
    .AddProvider(new ConsoleLoggerProvider())
    .UseFilter(new AuditOnlyFilter())
    .Build();
```
