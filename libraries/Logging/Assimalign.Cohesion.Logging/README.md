# Assimalign.Cohesion.Logging

Cohesion logging foundation. Defines `ILoggerEntry`, `ILogger`, `IScopedLogger`,
`ILoggerProvider`, `ILoggerFactory`, the fluent builder, the enricher contract, and the
default `LoggerFactory`/`LoggerFactoryBuilder` implementations.

Implementations:

- `Assimalign.Cohesion.Logging.Console` - console sink.
- `Assimalign.Cohesion.Logging.Debug` - `System.Diagnostics.Debug` sink.

See `docs/OVERVIEW.md` and `docs/DESIGN.md` for the full contract definition.
