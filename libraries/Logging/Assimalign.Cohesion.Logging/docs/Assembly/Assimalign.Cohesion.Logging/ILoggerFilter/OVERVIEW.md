# `Assimalign.Cohesion.Logging.ILoggerFilter`

Per-entry predicate hung off a `LoggerFilterRule`. Receives the complete `ILoggerEntry` so
filters can branch on category, level, attributes, exception, or any combination of them.

## Members

```csharp
string Name { get; }
bool ShouldLog(ILoggerEntry entry);
```

`Name` identifies the filter when reading `ILoggerFactory.Rules`. Names are not required to be
unique: one filter instance may sit on several rules.

Returning `true` admits the entry through this rule; returning `false` drops it for the
provider(s) the rule targets.

## Rules

- The filter runs only when its containing `LoggerFilterRule` has been selected for the
  current (provider, category) pair. Selection happens at logger creation time; the filter
  itself runs once per entry per matching provider.
- Filters run after the rule's `Level` check. If the rule has no `Level`, the filter is the
  sole gate.
- Implementations must be thread-safe; the filter runs on the caller's thread during
  `ILogger.Log(ILoggerEntry)`.
- A throwing filter is treated as `true` (admit). The composite swallows the exception so a
  bad filter cannot drop entries silently.

## Example

```csharp
internal sealed class AttributeKeyFilter : ILoggerFilter
{
    private readonly string _requiredKey;
    public AttributeKeyFilter(string requiredKey) { _requiredKey = requiredKey; }
    public string Name => "AttributeKey:" + _requiredKey;
    public bool ShouldLog(ILoggerEntry entry) => entry.Attributes.ContainsKey(_requiredKey);
}

ILoggerFactory factory = new LoggerFactoryBuilder()
    .AddProvider(new ConsoleLoggerProvider())
    .AddRule(new LoggerFilterRule(filter: new AttributeKeyFilter("audit")))
    .Build();
```
