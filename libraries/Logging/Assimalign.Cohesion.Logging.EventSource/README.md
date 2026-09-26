# Assimalign.Cohesion.Logging.EventSource

The in-process tap into `System.Diagnostics.Tracing` event sources for Cohesion logging. Every Cohesion
library reports its runtime diagnostics through an internal event source named for its assembly; this
package forwards those events — and any other event source you name — into an `ILoggerFactory`, one
`ILoggerEntry` per event, with the source name as the log category.

```csharp
using ILoggerFactory loggerFactory = new LoggerFactoryBuilder()
    .AddProvider(new ConsoleLoggerProvider())
    .AddRule("Assimalign.Cohesion.Connections", LogLevel.Debug)   // verbose connection detail
    .Build();

using IDisposable forwarding = loggerFactory.ForwardEventSources();
```

See `docs/OVERVIEW.md` and `docs/DESIGN.md` for the mapping, the level rules, and the failure model, and
`docs/EVENT_SOURCES.md` at the repository root for the event sources Cohesion ships.
