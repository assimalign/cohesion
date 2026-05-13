# `Assimalign.Cohesion.Logging.Console.ConsoleLoggerOptions`

Configuration shape for `ConsoleLoggerProvider`.

| Property | Type | Default | Description |
| --- | --- | --- | --- |
| `Output` | `TextWriter?` | `null` -> `System.Console.Out` at write time | Standard output writer. |
| `ErrorOutput` | `TextWriter?` | `null` -> `System.Console.Error` at write time | Error output writer. |
| `IncludeAttributes` | `bool` | `true` | Render structured attributes inline. |
| `IncludeException` | `bool` | `true` | Render exception `ToString()` on a follow-up line. |
| `IncludeParentId` | `bool` | `false` | Render `parentId=...` for scoped entries. |
| `Formatter` | `Action<ILogEntry, TextWriter>?` | `null` | Override the built-in renderer. |
