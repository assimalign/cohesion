# Assimalign.Cohesion.Logging.Debug

## Summary

Debug sink for Cohesion logging. Emits structured log entries through
`System.Diagnostics.Debug.WriteLine`, which surfaces in Visual Studio's Output window and any
other `Debug.Listeners` consumer. Gated by `Debugger.IsAttached` by default so the provider
stays silent in production unless a debugger is present.

## Status

- Status: Stable.
- Production source files: 3 (`DebugLoggerProvider`, `DebugLoggerOptions`, internal
  `DebugLogger`).
- Project references: `Assimalign.Cohesion.Logging`.
- Package references: None.
- `NotImplementedException` markers: 0.
- Tests: 10, covering writer routing, debugger gating, attribute and exception rendering,
  scope behavior, lifecycle, and argument validation.

## Primary Responsibilities

- Implement `ILoggerProvider` for the debug stream.
- Provide a programmatic override (`DebugLoggerOptions.Writer`) so tests and tools can
  capture output without touching the `Debug` listener collection.
- Honor `DebugLoggerOptions.EmitOnlyWhenDebuggerAttached` so the provider can be left
  registered in production builds.

## Configuration

`DebugLoggerOptions`:

| Option | Default | Notes |
| --- | --- | --- |
| `EmitOnlyWhenDebuggerAttached` | `true` | Gate emission on `Debugger.IsAttached`. |
| `IncludeAttributes` | `true` | Render structured attributes inline. |
| `IncludeException` | `true` | Render `exception.ToString()` on a follow-up line. |
| `Writer` | `null` | When set, the provider routes lines to the delegate instead of `Debug.WriteLine`. |

## Source Layout

- `src/DebugLoggerProvider.cs` - public registration entry point.
- `src/DebugLoggerOptions.cs` - configuration shape.
- `src/Internal/DebugLogger.cs` - per-category logger handed to callers.
- `src/Properties/AssemblyInfo.cs` - `InternalsVisibleTo` for tests.
