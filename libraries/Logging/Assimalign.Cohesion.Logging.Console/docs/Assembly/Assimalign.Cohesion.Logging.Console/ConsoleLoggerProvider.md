# `Assimalign.Cohesion.Logging.ConsoleLoggerProvider`

Console sink implementation of `Assimalign.Cohesion.Logging.ILoggerProvider`.

## Constructors

```csharp
public ConsoleLoggerProvider()
public ConsoleLoggerProvider(ConsoleLoggerOptions options)
```

The parameterless constructor uses default options. The second throws `ArgumentNullException`
when `options` is null.

## Members

| Member | Description |
| --- | --- |
| `Name` | Always `"Console"`. |
| `Create(string category)` | Returns a per-category logger. Throws `ObjectDisposedException` after `Dispose`. |
| `Dispose()` | Flushes both writers and marks the provider disposed. Subsequent operations no-op or throw. |

## Output Routing

| Entry level | Writer |
| --- | --- |
| `Error`, `Critical` | `Options.ErrorOutput` (defaults to `System.Console.Error` at write time). |
| Anything else | `Options.Output` (defaults to `System.Console.Out` at write time). |

## Notes

- Output is serialized through an internal `Lock` so concurrent log calls do not interleave.
- A caller-supplied `Formatter` overrides the built-in renderer; a throwing formatter is
  swallowed so the provider stays usable.
- `BeginScope` emits the seed entry so scope-open events are visible in the console.
