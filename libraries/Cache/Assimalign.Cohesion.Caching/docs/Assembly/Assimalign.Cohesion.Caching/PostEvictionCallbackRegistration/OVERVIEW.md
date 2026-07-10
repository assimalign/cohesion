# `Assimalign.Cohesion.Caching.PostEvictionCallbackRegistration`

Pairs a `PostEvictionDelegate` with caller-supplied state. Added to `ICacheEntry.PostEvictionCallbacks`
to fire after the entry leaves the cache.

## Constructor

```csharp
public PostEvictionCallbackRegistration(PostEvictionDelegate callback, object? state = null)
```

Throws `ArgumentNullException` when `callback` is null.

## Properties

| Property | Description |
| --- | --- |
| `EvictionCallback` | The delegate to invoke. |
| `State` | Caller state passed back to the callback. |

## Delegate signature

```csharp
public delegate void PostEvictionDelegate(
    object key,
    object? value,
    CacheEvictionReason reason,
    object? state);
```

## Notes

- Callbacks fire after the entry is removed from the cache, never before.
- An exception thrown by one callback does not prevent the remaining callbacks for the same
  entry from firing.
- Callbacks may run on any thread; do not assume the disposing thread.
