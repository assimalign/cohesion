# `Assimalign.Cohesion.Caching.InMemory.MemoryCache`

In-process implementation of `Assimalign.Cohesion.Caching.IMemoryCache`.

## Constructors

```csharp
public MemoryCache()
public MemoryCache(MemoryCacheOptions options)
```

`options` is required for the second overload. The parameterless overload uses default
`MemoryCacheOptions`. Both throw `ArgumentNullException` when the argument is null.

## Members

| Member | Description |
| --- | --- |
| `Count` | Number of entries currently held. |
| `TotalSize` | Cumulative size of every entry currently held. Always zero when `MemoryCacheOptions.SizeLimit` is null. |
| `CreateEntry(object key)` | Returns a `MemoryCacheEntry` configured for the cache. Commits on dispose. |
| `TryGetValue(object key, out object? value)` | Lookup. Also triggers an expiration scan if the throttle interval has elapsed. |
| `Remove(object key)` | Removes the entry if present; fires `Removed` callback. |
| `Clear()` | Removes every entry with reason `Removed`. |
| `Compact()` | Forces an expiration scan; useful for tests and diagnostics. |
| `Dispose()` | Evicts every entry with reason `Removed`. Subsequent operations throw `CacheException(Disposed)`. |

## Exceptions

| Exception | Condition |
| --- | --- |
| `ArgumentNullException` | A required argument was null. |
| `CacheException(Disposed)` | Operation called after `Dispose`. |
| `CacheException(InvalidEntry)` | Committed entry failed validation (non-positive expiration, negative size, missing size when `SizeLimit` is set). |
| `CacheException(CapacityExceeded)` | Committed entry's size alone exceeds `SizeLimit`. |

## Thread safety

Reads are lock-free. Writes and eviction synchronize through the underlying
`ConcurrentDictionary` and `Interlocked` primitives. The cache is safe to use from many
threads simultaneously; see `docs/DESIGN.md` for the model.
