# `Assimalign.Cohesion.Logging.LoggerExtensions`

Typed-level helpers over `ILogger`. Each helper builds a `LogEntry`, runs the level guard,
and writes to the logger.

## Methods

| Method | Level |
| --- | --- |
| `LogTrace(this ILogger, string category, string message, IReadOnlyDictionary<string, object?>? attributes = null)` | `Trace` |
| `LogDebug(this ILogger, string category, string message, IReadOnlyDictionary<string, object?>? attributes = null)` | `Debug` |
| `LogInformation(this ILogger, string category, string message, IReadOnlyDictionary<string, object?>? attributes = null)` | `Information` |
| `LogWarning(this ILogger, string category, string message, IReadOnlyDictionary<string, object?>? attributes = null)` | `Warning` |
| `LogError(this ILogger, string category, string message, Exception? exception = null, IReadOnlyDictionary<string, object?>? attributes = null)` | `Error` |
| `LogCritical(this ILogger, string category, string message, Exception? exception = null, IReadOnlyDictionary<string, object?>? attributes = null)` | `Critical` |
| `Log(this ILogger, LogLevel level, string category, string message, Exception? exception = null, IReadOnlyDictionary<string, object?>? attributes = null)` | explicit |
| `BeginScope(this ILogger, string category, string message, IReadOnlyDictionary<string, object?>? attributes = null)` | scope helper |

## Short-circuiting

Each helper calls `ILogger.IsEnabled(level)` first; when the logger is not enabled, no
`LogEntry` is allocated.

## Argument validation

All helpers throw `ArgumentNullException` for a null logger and `ArgumentException` for an
empty `category`.
