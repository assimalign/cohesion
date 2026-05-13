# `Assimalign.Cohesion.Logging.ILoggerFactoryBuilder`

Fluent registration surface for `ILoggerFactory`. Build the factory in a single thread, then
publish the resulting (thread-safe) factory.

## Methods

| Method | Description |
| --- | --- |
| `AddProvider(ILoggerProvider provider)` | Registers a provider. Duplicate names throw `InvalidOperationException`. |
| `SetMinimumLevel(LogLevel level)` | Factory default minimum level. Defaults to `Information`. |
| `AddFilter(string categoryPrefix, LogLevel minimumLevel)` | Category-prefix override (longest prefix wins, case-insensitive). |
| `AddEnricher(ILogEnricher enricher)` | Adds an enricher to the pipeline. Enrichers run in registration order. |
| `Build()` | Materializes the factory. The builder is single-use; subsequent operations throw `InvalidOperationException`. |

## Exceptions

- `ArgumentNullException` for null `provider` or `enricher`.
- `ArgumentException` for an empty `categoryPrefix`.
- `InvalidOperationException` for duplicate provider names or reuse after `Build`.

## Implementation

`LoggerFactoryBuilder` is the default implementation. Builders are not thread-safe; treat
them as scratch space scoped to a single setup routine.
