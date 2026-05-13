# `Assimalign.Cohesion.Caching.CacheException`

Domain exception raised by Cohesion cache implementations. Carries a `CacheErrorCode` so
callers can branch on the failure mode without text matching.

## Constructor

```csharp
public CacheException(CacheErrorCode errorCode, string message, Exception? innerException = null)
```

## Property

| Property | Description |
| --- | --- |
| `ErrorCode` | The diagnostics code attached to the exception. |

## `CacheErrorCode` values

| Code | Description |
| --- | --- |
| `Unknown` | Unclassified error. |
| `Disposed` | The cache has been disposed. |
| `InvalidEntry` | Committed entry failed validation (negative size, non-positive expiration, etc.). |
| `CapacityExceeded` | The entry could not fit even after eviction. |
