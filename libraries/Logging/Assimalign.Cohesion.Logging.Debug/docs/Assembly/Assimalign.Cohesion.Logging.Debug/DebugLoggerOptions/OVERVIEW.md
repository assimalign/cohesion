# `Assimalign.Cohesion.Logging.Debug.DebugLoggerOptions`

Configuration shape for `DebugLoggerProvider`.

| Property | Type | Default | Description |
| --- | --- | --- | --- |
| `EmitOnlyWhenDebuggerAttached` | `bool` | `true` | When true and `Writer` is null, the provider emits only while a debugger is attached. |
| `IncludeAttributes` | `bool` | `true` | Render structured attributes inline. |
| `IncludeException` | `bool` | `true` | Render exception `ToString()` on a follow-up line. |
| `Writer` | `Action<string>?` | `null` | Override `Debug.WriteLine`; useful for tests and tools. |
