# `Assimalign.Cohesion.Logging.CategoryLoggerFilter`

Built-in `ILoggerFilter` that selects entries by category prefix. Each rule pairs a
case-insensitive category prefix with the minimum log level required for that prefix; the
longest matching prefix wins. Entries whose category matches no rule are admitted unchanged
(deferring to the factory-wide minimum level).

## Constructor

```csharp
public CategoryLoggerFilter(IEnumerable<KeyValuePair<string, LogLevel>> rules)
```

Throws `ArgumentNullException` for null `rules` and `ArgumentException` when any rule's
prefix is null or empty.

## Method

`ShouldLog(ILoggerEntry entry)` returns:

- `true` when no rule prefix matches the entry's category, or when the entry's level is at or
  above the level of the longest matching rule.
- `false` otherwise.

## Used by

`LoggerFactoryBuilder.AddFilter(prefix, level)` accumulates rules and constructs the filter
at `Build()` time.
