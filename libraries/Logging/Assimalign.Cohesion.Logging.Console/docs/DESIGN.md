# Assimalign.Cohesion.Logging.Console Design

## Design Intent

The provider package is meant to keep rendering and device-specific concerns out of the logging core. The current implementation is still partial, but the intended seam is clear: plug a console logger provider into LoggerFactory.

## Implementation Note

Examples below document the intended public shape; some members still throw NotImplementedException today.

## Architecture

- ConsoleLoggerProvider is the package entry point for registration into a factory.
- ConsoleLogger is the provider-specific logger implementation.
- Formatting and console output behavior belong here rather than in the shared logging contracts.

## Layout Example

```text
Assimalign.Cohesion.Logging.Console/
  src/
    Assimalign.Cohesion.Logging.Console.csproj
    Internal/
  tests/
  docs/
    OVERVIEW.md
    DESIGN.md
```

## Example 1: Construct the provider directly

```csharp
var provider = new ConsoleLoggerProvider();
ILogger logger = provider.Create("App");
```

## Example 2: Compose the provider into a logger factory

```csharp
ILoggerFactory factory = new LoggerFactory([new ConsoleLoggerProvider()]);
ILogger logger = factory.Create("Console");
```
