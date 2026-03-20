# Assimalign.Cohesion.Logging Design

## Design Intent

The package separates log creation, provider composition, and log event shape so transports and providers can evolve independently. Named logger caching inside the factory keeps logger creation cheap for callers.

## Architecture

- ILogger, ILoggerProvider, ILoggerFactory, and the scope interfaces form the public contract surface.
- LoggerEntry carries the structured data for a log event.
- LoggerFactory composes providers into cached named loggers instead of forcing every caller to manage providers directly.

## Layout Example

```text
Assimalign.Cohesion.Logging/
  src/
    Assimalign.Cohesion.Logging.csproj
    Abstractions/
    Extensions/
    Internal/
    ValueObjects/
  tests/
  docs/
    OVERVIEW.md
    DESIGN.md
```

## Example 1: Create and use a named logger

```csharp
ILoggerFactory factory = new LoggerFactory([new ConsoleLoggerProvider()]);
ILogger logger = factory.Create("App");

logger.Log(new LoggerEntry(LogId.New(), LogLevel.Information, "Started"));
```

## Example 2: Create a scope around a log flow

```csharp
using IScopedLogger scope = logger.BeginScope(
    new LoggerEntry(LogId.New(), LogLevel.Trace, "Request scope"));
```
