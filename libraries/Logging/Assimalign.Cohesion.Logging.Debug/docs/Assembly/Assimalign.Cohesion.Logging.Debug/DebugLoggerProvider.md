# `Assimalign.Cohesion.Logging.Debug.DebugLoggerProvider`

Debug-stream sink implementation of `Assimalign.Cohesion.Logging.ILoggerProvider`.

## Constructors

```csharp
public DebugLoggerProvider()
public DebugLoggerProvider(DebugLoggerOptions options)
```

The parameterless constructor uses default options. The second throws `ArgumentNullException`
when `options` is null.

## Members

| Member | Description |
| --- | --- |
| `Name` | Always `"Debug"`. |
| `Create(string category)` | Returns a per-category logger. Throws `ObjectDisposedException` after `Dispose`. |
| `Dispose()` | Marks the provider disposed; subsequent operations no-op or throw. |

## Gating

- When `Options.Writer` is null and `Options.EmitOnlyWhenDebuggerAttached` is `true`
  (default), the provider emits only while `System.Diagnostics.Debugger.IsAttached` is
  `true`.
- Setting `Options.Writer` automatically bypasses the gate; the caller has opted in to
  capture lines.

## Notes

- `BeginScope` emits the seed entry through the write path so scope-open events appear in
  the debug stream.
- A throwing `Writer` is swallowed; the provider stays usable.
