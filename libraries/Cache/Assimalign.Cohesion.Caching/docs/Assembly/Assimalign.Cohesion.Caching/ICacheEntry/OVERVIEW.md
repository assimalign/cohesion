# `Assimalign.Cohesion.Caching.ICacheEntry`

Mutable, disposable handle to a cache entry. Configure it, then dispose to commit.

## Properties

| Property | Type | Description |
| --- | --- | --- |
| `Key` | `object` | The key supplied to `ICache.CreateEntry`. |
| `Value` | `object?` | The value stored on commit. Defaults to `null`. |
| `AbsoluteExpiration` | `DateTimeOffset?` | Explicit UTC deadline. |
| `AbsoluteExpirationRelativeToNow` | `TimeSpan?` | Offset evaluated at commit. Must be > 0. |
| `SlidingExpiration` | `TimeSpan?` | Idle window. Must be > 0. Never extends past `AbsoluteExpiration`. |
| `ExpirationTokens` | `IList<IChangeToken>` | Tokens that, on notification, evict the entry with `TokenExpired`. |
| `PostEvictionCallbacks` | `IList<PostEvictionCallbackRegistration>` | Callbacks fired after eviction. |
| `Priority` | `CacheEntryPriority` | Eviction priority. Defaults to `Normal`. |
| `Size` | `long?` | Optional logical size. Required when the cache enforces a size limit. |

## Lifecycle

- Configure properties in any order.
- Call `Dispose` exactly once to commit. Calling `Dispose` a second time is a no-op.
- Disposing without ever setting `Value` commits an entry whose stored value is `null`.

## Validation on commit

Implementations should reject:

- `AbsoluteExpirationRelativeToNow <= TimeSpan.Zero` -> `CacheException(InvalidEntry)`.
- `SlidingExpiration <= TimeSpan.Zero` -> `CacheException(InvalidEntry)`.
- `Size < 0` -> `CacheException(InvalidEntry)`.

## Example

```csharp
using var entry = cache.CreateEntry("user:42");

entry.Value = profile;
entry.AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(10);
entry.SlidingExpiration = TimeSpan.FromMinutes(2);
entry.Priority = CacheEntryPriority.High;
entry.PostEvictionCallbacks.Add(new PostEvictionCallbackRegistration(
    (key, _, reason, _) => logger.LogInformation("{Key} evicted: {Reason}", key, reason)));
```
