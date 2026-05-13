# `Assimalign.Cohesion.Logging.ILoggerFactoryBuilder`

Fluent registration surface for `ILoggerFactory`. Build the factory in a single thread, then
publish the resulting (thread-safe) factory.

## Methods

| Method | Description |
| --- | --- |
| `AddProvider(ILoggerProvider provider)` | Registers a provider. Duplicate names throw `InvalidOperationException`. |
| `SetMinimumLevel(LogLevel level)` | Factory-wide minimum level (the unconditional floor). Defaults to `Information`. |
| `AddFilter(string categoryPrefix, LogLevel minimumLevel)` | Adds a category-prefix rule. Rules accumulate into a `CategoryLoggerFilter` at build time; the longest matching prefix wins. The filter can only raise the minimum, never lower the factory floor. |
| `UseFilter(ILoggerFilter filter)` | Plugs in an arbitrary per-entry filter. When combined with `AddFilter`, both filters must accept the entry. |
| `AddEnricher(ILoggerEnricher enricher)` | Adds an enricher to the pipeline. Enrichers run in registration order. |
| `Build()` | Materializes the factory. The builder is single-use; subsequent operations throw `InvalidOperationException`. |

## Exceptions

- `ArgumentNullException` for null `provider`, `enricher`, or `filter`.
- `ArgumentException` for an empty `categoryPrefix`.
- `InvalidOperationException` for duplicate provider names or reuse after `Build`.

## Implementation

`LoggerFactoryBuilder` is the default implementation. Builders are not thread-safe; treat
them as scratch space scoped to a single setup routine.
