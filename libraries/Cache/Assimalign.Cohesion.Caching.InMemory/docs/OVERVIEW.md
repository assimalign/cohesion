# Assimalign.Cohesion.Caching.InMemory

## Summary

In-process implementation of the Cohesion cache contracts. `MemoryCache` is a thread-safe,
AOT-friendly cache that honors every expiration, eviction, and invalidation rule defined by
`Assimalign.Cohesion.Caching`. It is the default local cache for Cohesion services.

## Status

- Status: Stable.
- Production source files: 4 (`MemoryCache`, `MemoryCacheOptions`, internal `MemoryCacheEntry`,
  internal `StoredEntry`).
- Project references: `Assimalign.Cohesion.Caching` (foundation contracts).
- Package references: None.
- Test files: 5 (35 in-memory unit tests, 9 contract-compatibility tests, 4 performance
  baselines, plus options validation and reusable test helpers). Approximately 91 % line and
  89 % branch coverage.

## Primary Responsibilities

- Implement `ICache` with a `ConcurrentDictionary`-backed storage layer.
- Honor `AbsoluteExpiration`, `AbsoluteExpirationRelativeToNow`, `SlidingExpiration`, and
  `ExpirationTokens` from the foundation contract.
- Run lazy expiration scans on access plus throttled bulk scans bounded by
  `MemoryCacheOptions.ExpirationScanFrequency`.
- Enforce `MemoryCacheOptions.SizeLimit` with priority-aware, LRU-as-tiebreaker capacity
  eviction.
- Fire post-eviction callbacks with the correct `CacheEvictionReason` for every removal path.

## Project Boundaries

- Depends on `Assimalign.Cohesion.Caching` for contracts and on
  `Assimalign.Cohesion.Core.System.Threading.IChangeToken` for token-driven invalidation.
  No `Microsoft.Extensions.*` dependencies.
- The in-memory specifics (locking strategy, dictionary choice, scan cadence) are internal
  and may evolve as long as the foundation contracts continue to be honored.

## Configuration

`MemoryCacheOptions`:

| Option | Default | Notes |
| --- | --- | --- |
| `ExpirationScanFrequency` | 1 minute | Upper bound on background expiration scan cadence. |
| `SizeLimit` | `null` | When set, every committed entry must declare `Size`. |
| `CompactionPercentage` | 0.05 | Fraction of `SizeLimit` released during a capacity compaction. |
| `TimeProvider` | `TimeProvider.System` | Used for expiration and access tracking. Override for deterministic tests. |

## Source Layout

- `src/MemoryCache.cs` - production cache implementation.
- `src/MemoryCacheOptions.cs` - configuration knobs.
- `src/Internal/MemoryCacheEntry.cs` - scratch entry returned from `CreateEntry`.
- `src/Internal/StoredEntry.cs` - immutable per-entry record kept in the dictionary.
- `src/Properties/AssemblyInfo.cs` - `InternalsVisibleTo` for tests.
