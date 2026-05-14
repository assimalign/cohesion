# `Assimalign.Cohesion.Logging.ILoggerFactoryBuilder`

Fluent registration surface for `ILoggerFactory`. Build the factory in a single thread, then
publish the resulting (thread-safe) factory.

## Methods

| Method | Description |
| --- | --- |
| `AddProvider(ILoggerProvider provider)` | Registers a provider. Duplicate names throw `InvalidOperationException`. |
| `SetMinimumLevel(LogLevel level)` | Factory-wide minimum level. Used as the fallback when no `LoggerFilterRule` matches a (provider, category) pair. Defaults to `Information`. |
| `AddRule(LoggerFilterRule rule)` | Adds a rule to the filter ruleset. Rules are evaluated per (provider, category) pair via the selection algorithm documented on `LoggerFilterRule`. |
| `AddRule(string categoryPrefix, LogLevel minimumLevel)` | Convenience overload that constructs and adds a `LoggerFilterRule { Category = categoryPrefix, Level = minimumLevel }`. |
| `AddEnricher(ILoggerEnricher enricher)` | Adds an enricher to the pipeline. Enrichers run in registration order. |
| `Build()` | Materializes the factory. The builder is single-use; subsequent operations throw `InvalidOperationException`. |

## Exceptions

- `ArgumentNullException` for null `provider`, `rule`, or `enricher`.
- `ArgumentException` for an empty `categoryPrefix`.
- `InvalidOperationException` for duplicate provider names or reuse after `Build`.

## Implementation

`LoggerFactoryBuilder` is the default implementation. Builders are not thread-safe; treat
them as scratch space scoped to a single setup routine.
