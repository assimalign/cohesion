# `Assimalign.Cohesion.Logging.ILogger`

Writes structured log events to one or more sinks. Loggers are normally obtained from
`ILoggerFactory.Create(category)`.

## Members

| Member | Description |
| --- | --- |
| `bool IsEnabled(LogLevel level)` | True when at least one sink would accept an entry at `level`. Callers MAY use this to short-circuit expensive payload construction. |
| `void Log(ILogEntry entry)` | Writes the entry to every underlying sink. Per-sink failures are isolated. |
| `IScopedLogger BeginScope(ILogEntry entry)` | Opens a scope. Entries written through the scope inherit `entry.Id` as their `ParentId`. |

## Thread safety

Implementations MUST be thread-safe. The composite logger returned from
`LoggerFactory.Create` synchronizes fan-out through the underlying providers; each provider
is expected to be safe for concurrent calls.

## Exceptions

- `ArgumentNullException` when `entry` is null.

## Typed helpers

Use `LoggerExtensions` for the common shapes (`LogTrace`, `LogDebug`, `LogInformation`,
`LogWarning`, `LogError`, `LogCritical`, `Log(level, ...)`). The helpers short-circuit on
`IsEnabled` automatically.
