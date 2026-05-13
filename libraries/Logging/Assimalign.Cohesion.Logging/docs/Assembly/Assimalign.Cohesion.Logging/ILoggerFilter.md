# `Assimalign.Cohesion.Logging.ILoggerFilter`

Per-entry gate consulted after the factory-wide minimum level but before fan-out. Receives the
complete `ILoggerEntry` so the filter can branch on category, level, attributes, exception, or
any combination of them.

## Method

```csharp
bool ShouldLog(ILoggerEntry entry);
```

Returning `true` admits the entry to the fan-out stage; returning `false` drops it before any
provider sees it.

## Rules

- Filters run after `LoggerFactoryOptions.MinimumLevel`. They can only further restrict, never
  loosen, the factory floor.
- Implementations must be thread-safe; the filter runs on the caller's thread during
  `ILogger.Log(ILoggerEntry)`.
- A throwing filter is treated as `true` (admit). The composite swallows the exception so a
  bad filter cannot drop entries silently.

## Built-in filter

`CategoryLoggerFilter` implements a category-prefix → minimum-level rule set (longest prefix
wins). The fluent `AddFilter(prefix, level)` helper on `LoggerFactoryBuilder` accumulates
rules into one of these filters at build time. To plug in arbitrary logic, call
`UseFilter(myFilter)` instead.

## Example

```csharp
internal sealed class AttributeKeyFilter : ILoggerFilter
{
    private readonly string _requiredKey;
    public AttributeKeyFilter(string requiredKey) { _requiredKey = requiredKey; }
    public bool ShouldLog(ILoggerEntry entry) => entry.Attributes.ContainsKey(_requiredKey);
}

ILoggerFactory factory = new LoggerFactoryBuilder()
    .AddProvider(new ConsoleLoggerProvider())
    .UseFilter(new AttributeKeyFilter("audit"))
    .Build();
```
