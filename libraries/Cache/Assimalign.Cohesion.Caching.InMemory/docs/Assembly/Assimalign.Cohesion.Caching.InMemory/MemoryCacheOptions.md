# `Assimalign.Cohesion.Caching.InMemory.MemoryCacheOptions`

Configuration shape for `MemoryCache`.

## Properties

| Property | Type | Default | Description |
| --- | --- | --- | --- |
| `ExpirationScanFrequency` | `TimeSpan` | 1 minute | Upper bound on the bulk-scan interval. Must be greater than zero. |
| `SizeLimit` | `long?` | `null` | When set, every committed entry must declare `Size` and entries are evicted by priority + LRU when the limit is exceeded. Must be greater than or equal to zero. |
| `CompactionPercentage` | `double` | 0.05 | Fraction of `SizeLimit` released by a capacity-driven compaction. Must be in `(0, 1]`. |
| `TimeProvider` | `TimeProvider?` | `null` | Clock used for expiration / access. Defaults to `TimeProvider.System` when null. |

## Validation

Each setter validates its input and throws `ArgumentOutOfRangeException` for invalid values
(zero or negative `ExpirationScanFrequency`, negative `SizeLimit`, out-of-range
`CompactionPercentage`).

## Usage

```csharp
var options = new MemoryCacheOptions
{
    ExpirationScanFrequency = TimeSpan.FromSeconds(30),
    SizeLimit = 4_096,
    CompactionPercentage = 0.25,
};

using var cache = new MemoryCache(options);
```
