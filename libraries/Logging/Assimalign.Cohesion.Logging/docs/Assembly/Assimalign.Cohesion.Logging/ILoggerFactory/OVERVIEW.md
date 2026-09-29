# `Assimalign.Cohesion.Logging.ILoggerFactory`

Roots the logging pipeline. Caches composite loggers per category, owns the registered
providers' and forwarders' lifecycle.

## Properties

The factory's composition is fixed at construction. Each property is a read-only view of it, in
registration order; none can be cast back to the factory's own array.

| Property | Description |
| --- | --- |
| `Providers` | The providers fan-out targets registered with the factory. |
| `Enrichers` | The enrichers every entry passes through before fan-out, in execution order. |
| `Rules` | The filter rules resolved per (provider, category) pair. Each `ILoggerFilter` lives on a rule (`LoggerFilterRule.Filter`), so the rules, not a flat filter list, are what the factory exposes. |
| `Forwarders` | The forwarders the factory owns, in creation order. Empty while they are being created. A forwarder the caller created against the factory (for example with `ForwardEventSources()`) is not listed. |

## Methods

| Method | Description |
| --- | --- |
| `ILogger Create(string category)` | Returns the cached composite logger for `category` (case-insensitive). |
| `void Dispose()` | Disposes every owned forwarder (newest first, see `ILoggerForwarder`) and then every owned provider; subsequent operations throw `ObjectDisposedException`. |

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
    .AddRule("App.Network", LogLevel.Debug)
    .AddEnricher(new ProcessEnricher())
    .Build();

foreach (ILoggerEnricher enricher in factory.Enrichers)
{
    Console.WriteLine(enricher.Name);
}
```

The factory's `Create` is thread-safe and lock-free for the cache hit path.
