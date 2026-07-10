# `Assimalign.Cohesion.Caching.ICache`

Root contract for any Cohesion cache. Implementations are thread-safe and synchronous.

## Members

| Member | Description |
| --- | --- |
| `CreateEntry(object key)` | Returns a disposable `ICacheEntry`. Configure it, then dispose to commit. Throws `ArgumentNullException` for a null key. |
| `TryGetValue(object key, out object? value)` | Lookup. Returns `true` when an entry exists and has not been evicted. |
| `Remove(object key)` | Removes the entry if present. Fires post-eviction callbacks with `Removed`. |
| `Clear()` | Removes every entry. Fires post-eviction callbacks with `Removed` for each. |
| `Dispose()` | Evicts every entry with reason `Removed`; subsequent operations throw `CacheException(Disposed)`. |

## Notes

- Keys are `object` for heterogeneous workloads. Strongly typed access lives on
  `CacheExtensions` (`Get<T>`, `Set<T>`, `TryGetValue<T>`, `GetOrCreate<T>`).
- Implementations must be thread-safe. Reads should be lock-free.
- Disposing an `ICacheEntry` is the only commit path; configuring the entry without disposing
  is a no-op against the cache.
