# `Assimalign.Cohesion.Logging.ILoggerFactory`

Roots the logging pipeline. Caches composite loggers per category, owns the registered
providers' lifecycle.

## Properties

| Property | Description |
| --- | --- |
| `Providers` | The providers fan-out targets registered with the factory. |

## Methods

| Method | Description |
| --- | --- |
| `ILogger Create(string category)` | Returns the cached composite logger for `category` (case-insensitive). |
| `void Dispose()` | Disposes every owned provider; subsequent operations throw `ObjectDisposedException`. |

## Exceptions

- `ArgumentException` for null or empty `category`.
- `ObjectDisposedException` when the factory has been disposed.

## Implementation

`LoggerFactory` is the default implementation. Build it through `LoggerFactoryBuilder`:

```csharp
ILoggerFactory factory = new LoggerFactoryBuilder()
    .AddProvider(new ConsoleLoggerProvider())
    .AddProvider(new DebugLoggerProvider())
    .SetMinimumLevel(LogLevel.Information)
    .AddFilter("App.Network", LogLevel.Debug)
    .AddEnricher(new ProcessEnricher())
    .Build();
```

The factory's `Create` is thread-safe and lock-free for the cache hit path.
