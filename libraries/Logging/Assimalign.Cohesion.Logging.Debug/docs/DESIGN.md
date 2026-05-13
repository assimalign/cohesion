# Assimalign.Cohesion.Logging.Debug Design

## Design Intent

The debug sink mirrors the console provider's responsibility narrowly: render an
`ILoggerEntry` to text and emit through `System.Diagnostics.Debug.WriteLine`. It exists so a
developer running under a debugger sees Cohesion log events in the IDE output window without
setting up any additional sinks.

## Render Format

One line per entry:

```text
[<timestamp ISO 8601>] [<LEVEL>] <category>: <message>{ <k>=<v>, ... }
```

When the entry carries an exception and `IncludeException` is `true`, the renderer writes
`exception.ToString()` on the following line.

## Debugger Gate

`EmitOnlyWhenDebuggerAttached` (default `true`) short-circuits the provider when no debugger
is attached, so the provider can be left in the factory in production builds without
incurring rendering cost. Setting `DebugLoggerOptions.Writer` automatically bypasses the gate
because the caller has explicitly opted to capture lines.

## Scope Behavior

`BeginScope` emits the seed entry through the write path so scope-open events appear in the
debug stream. Nested scopes work the same way; parent-id stamping is the composite layer's
job.

## Writer Override

`DebugLoggerOptions.Writer` accepts an `Action<string>` that receives each rendered line.
Use this for tests (capture into a list) or for routing to a custom debug pipeline. Writer
exceptions are swallowed so a misbehaving consumer cannot break the provider.

## AOT and Trimming

- No reflection.
- `System.Diagnostics.Debug` is part of the core BCL surface and trim-safe.
- The provider is suitable for NativeAOT publish without additional roots.

## Layout

```text
Assimalign.Cohesion.Logging.Debug/
  src/
    Assimalign.Cohesion.Logging.Debug.csproj
    DebugLoggerProvider.cs
    DebugLoggerOptions.cs
    Internal/
      DebugLogger.cs
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
    .AddProvider(new DebugLoggerProvider())
    .AddProvider(new ConsoleLoggerProvider())
    .SetMinimumLevel(LogLevel.Information)
    .Build();

ILogger logger = factory.Create("App");
logger.LogInformation("App", "startup",
    attributes: new Dictionary<string, object?> { ["version"] = "1.0" });

// For tests: capture output without touching System.Diagnostics.Debug listeners.
var captured = new List<string>();
using var debugProvider = new DebugLoggerProvider(new DebugLoggerOptions
{
    Writer = captured.Add,
});
```
