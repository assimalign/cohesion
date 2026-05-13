# `Assimalign.Cohesion.Caching.CacheEntryPriority`

Relative eviction priority for `ICacheEntry`.

| Value | Ordinal | Description |
| --- | --- | --- |
| `Low` | 0 | First to be evicted under capacity pressure. |
| `Normal` | 1 | Default. |
| `High` | 2 | Evicted only after lower-priority entries have been removed. |
| `NeverRemove` | 3 | Exempt from capacity-driven eviction. Still removed by explicit `Remove`, expiration, or token invalidation. |

## Usage

```csharp
using var entry = cache.CreateEntry("hot-config");
entry.Value = configuration;
entry.Priority = CacheEntryPriority.High;
```
