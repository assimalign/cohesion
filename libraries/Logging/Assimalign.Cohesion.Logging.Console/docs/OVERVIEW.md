# Assimalign.Cohesion.Logging.Console

## Summary

Console sink for Cohesion logging. Writes structured log entries to a pair of
`System.IO.TextWriter`s (one for general output, one for error-level output). Supports a
caller-supplied formatter for custom rendering and short-circuits the rendering pipeline when
no entry would be emitted.

## Status

- Status: Stable.
- Production source files: 3 (`ConsoleLoggerProvider`, `ConsoleLoggerOptions`, internal
  `ConsoleLogger`) plus the internal `ConsoleLogFormatter` renderer.
- Project references: `Assimalign.Cohesion.Logging`.
- Package references: None.
- `NotImplementedException` markers: 0.
- Tests: 12, covering output routing, attribute rendering, exception rendering, custom
  formatter override, scope behavior, lifecycle, and argument validation.

## Primary Responsibilities

- Implement `ILoggerProvider` so the console sink can be registered with
  `LoggerFactoryBuilder.AddProvider`.
- Render `ILoggerEntry` to a textual line, including timestamp, level, category, message,
  attributes, and exception (each controlled by an option flag).
- Route entries at `LogLevel.Error` or above to the error writer; everything else goes to
  the standard writer.

## Configuration

`ConsoleLoggerOptions`:

| Option | Default | Notes |
| --- | --- | --- |
| `Output` | `null` -> `System.Console.Out` at write time | Standard writer. |
| `ErrorOutput` | `null` -> `System.Console.Error` at write time | Error writer. |
| `IncludeAttributes` | `true` | Render structured attributes inline. |
| `IncludeException` | `true` | Render `ToString()` of the entry's exception on a follow-up line. |
| `IncludeParentId` | `false` | Render `parentId=...` for scoped entries. |
| `Formatter` | `null` | Override the built-in renderer. |

## Source Layout

- `src/ConsoleLoggerProvider.cs` - public registration entry point.
- `src/ConsoleLoggerOptions.cs` - configuration shape.
- `src/Internal/ConsoleLogger.cs` - per-category logger handed to callers.
- `src/Internal/ConsoleLogFormatter.cs` - built-in renderer.
- `src/Properties/AssemblyInfo.cs` - `InternalsVisibleTo` for tests.
