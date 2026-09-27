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
| `AddForwarder(Func<ILoggerFactory, ILoggerForwarder> create)` | Registers a forwarder. The built factory calls `create` once, with itself, as the last step of its construction, and disposes the forwarder (newest first) before its providers. See `ILoggerForwarder`. |
| `Build()` | Materializes the factory and creates its forwarders. The builder is single-use; subsequent operations throw `InvalidOperationException`. If a forwarder registration throws, the forwarders already created and the providers are disposed and the exception propagates. |

## Exceptions

- `ArgumentNullException` for null `provider`, `rule`, `enricher`, or `create`.
- `ArgumentException` for an empty `categoryPrefix`.
- `InvalidOperationException` for duplicate provider names, reuse after `Build`, or a forwarder
  registration that returns `null`.

## Implementation

`LoggerFactoryBuilder` is the default implementation. Builders are not thread-safe; treat
them as scratch space scoped to a single setup routine.
