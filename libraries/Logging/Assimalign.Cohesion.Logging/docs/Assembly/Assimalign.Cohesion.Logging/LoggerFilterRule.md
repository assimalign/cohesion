# `Assimalign.Cohesion.Logging.LoggerFilterRule`

One entry in a Cohesion logging filter ruleset. Rules describe how entries flowing to a
specific provider for a specific category should be gated.

## Constructor

```csharp
public LoggerFilterRule(
    Type? providerType = null,
    string? category = null,
    LogLevel? level = null,
    ILoggerFilter? filter = null);
```

All parameters are optional.

## Properties

| Property | Type | Description |
| --- | --- | --- |
| `ProviderType` | `Type?` | The provider type this rule targets. `null` means "applies to any provider that has no type-specific rule." |
| `Category` | `string?` | The category prefix this rule targets (case-insensitive). `null` means "applies to any category in the candidate set." |
| `Level` | `LogLevel?` | Minimum log level required for this rule to admit an entry. `null` means "no level constraint." |
| `Filter` | `ILoggerFilter?` | Per-entry predicate that further screens entries that already passed `Level`. `null` means "no extra filter." |

## Selection algorithm

For a given (provider, category) pair the factory selects ONE winning rule from the ruleset
following this algorithm:

1. **Provider-type filter** — rules with `ProviderType == providerType` are preferred. If
   none match, rules with `ProviderType == null` form the candidate set.
2. **Longest category match** — among the candidates, rules whose `Category` is the longest
   matching prefix of the entry's category are kept.
3. **No-category fallback** — if no rule matched by category, rules with `Category == null`
   are kept (still from the candidate set).
4. **Single survivor** — if exactly one rule remains, it wins.
5. **Last among ties** — if multiple rules survive, the LAST one registered wins.
6. **Global minimum** — if no rule applies at all, `LoggerFactoryOptions.MinimumLevel` is
   used as the gate.

## Examples

```csharp
// "Anything Console emits from App.Network logs at Debug+"
new LoggerFilterRule(
    providerType: typeof(ConsoleLoggerProvider),
    category: "App.Network",
    level: LogLevel.Debug);

// "Drop any entry that lacks audit=true on its attributes"
new LoggerFilterRule(filter: new AuditOnlyFilter());

// "Globally lower the floor to Trace for the entire App tree"
new LoggerFilterRule(category: "App", level: LogLevel.Trace);
```
