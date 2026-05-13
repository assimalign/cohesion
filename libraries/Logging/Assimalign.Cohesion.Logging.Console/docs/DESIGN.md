# Assimalign.Cohesion.Logging.Console Design

## Design Intent

Console rendering is intentionally simple. The provider's job is to:

1. Decide whether an entry should be written at all (level guard).
2. Pick the appropriate writer (`Output` for non-errors, `ErrorOutput` for errors).
3. Render the entry to a text line and flush.

Everything else (filtering, scope chaining, fan-out across multiple providers) is the
factory's responsibility; the console provider does not duplicate that logic.

## Render Format

The built-in renderer emits one line per entry:

```text
[<timestamp ISO 8601>] [<LEVEL>] <category>: <message>{ <k>=<v>, ... }[ parentId=<id>]
```

When the entry carries an exception and `IncludeException` is `true`, the renderer writes
`exception.ToString()` on the following line.

Custom formatters take precedence over the built-in renderer.

## Concurrency

A single `Lock` instance inside the provider serializes writes so concurrent log calls do not
interleave on the same writer. Writes flush after each entry; callers may share
`Console.Out` / `Console.Error` with other components without worrying about half-written
lines from the logger.

Disposal flushes both writers and flips a disposed flag; further calls become no-ops, and
subsequent `Create` calls throw `ObjectDisposedException`.

## Scope Behavior

`BeginScope` emits the seed entry through the standard write path so the developer sees
"scope opened" events in the console. Scoped loggers also pass through subsequent entries.
The composite layer handles parent-id stamping; the console provider just renders whatever
arrives.

If the supplied `Formatter` throws, the provider swallows the exception so a bad formatter
cannot bring down the logging pipeline.

## AOT and Trimming

- No reflection, no dynamic dispatch.
- The renderer uses `TextWriter` directly; no JSON / serializer assemblies are pulled in.
- Suitable for NativeAOT publish without additional roots.

## Layout

```text
Assimalign.Cohesion.Logging.Console/
  src/
    Assimalign.Cohesion.Logging.Console.csproj
    ConsoleLoggerProvider.cs
    ConsoleLoggerOptions.cs
    Internal/
      ConsoleLogger.cs
      ConsoleLogFormatter.cs
    Properties/
      AssemblyInfo.cs
  tests/
  docs/
    OVERVIEW.md
    DESIGN.md
```

## Example

```csharp
ILoggerFactory factory = new LoggerFactoryBuilder()
    .AddProvider(new ConsoleLoggerProvider(new ConsoleLoggerOptions
    {
        IncludeParentId = true,
        IncludeAttributes = true,
    }))
    .SetMinimumLevel(LogLevel.Information)
    .Build();

ILogger logger = factory.Create("App");
logger.LogInformation("App", "started", new Dictionary<string, object?> { ["version"] = "1.0" });

using IScopedLogger scope = logger.BeginScope("App", "request scope");
scope.LogWarning("App", "slow response", attributes: new Dictionary<string, object?> { ["ms"] = 1200 });
```
