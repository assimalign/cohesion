# `Assimalign.Cohesion.Caching.CacheEvictionReason`

Reason reported to `PostEvictionDelegate` callbacks.

| Value | Ordinal | When the cache reports it |
| --- | --- | --- |
| `None` | 0 | The entry has not been evicted. |
| `Removed` | 1 | Explicit `Remove(key)` or `Clear()`. Also reported on cache disposal. |
| `Replaced` | 2 | A new value was committed for the same key. |
| `Expired` | 3 | Absolute or sliding expiration elapsed. |
| `TokenExpired` | 4 | One of the entry's `ExpirationTokens` fired. |
| `Capacity` | 5 | The cache evicted the entry to release space. |
