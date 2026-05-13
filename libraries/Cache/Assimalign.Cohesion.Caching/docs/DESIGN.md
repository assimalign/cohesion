# Assimalign.Cohesion.Caching Design

## Design Intent

The root cache package is intentionally a contract surface only. The Cohesion platform has
two long-term cache shapes (in-process and distributed) and several near-term consumers
that will use them; the foundation library exists so those consumers can express their
caching dependency without committing to a storage strategy. Concrete cache runtimes live
in sibling implementation packages.

This document records the explicit contract decisions and how implementations are expected
to honor them.

## Contract Decisions

### Key Typing

Keys are `object`. Cache keys are routinely heterogeneous (strings, composite tuples,
strongly-typed identifier records, opaque correlation tokens) and a generic `TKey` would
constrain the surface without buying type safety in most call sites. Implementations are
free to wrap typed key helpers on top, but the core contract takes any non-null key.

### Value Typing

`ICache.TryGetValue` returns `object?`. Strongly typed access lives on `CacheExtensions`
via `Get<TValue>`, `Set<TValue>`, `TryGetValue<TValue>`, and `GetOrCreate<TValue>`. Going
through the typed extension API yields the default for the requested type when the stored
value is of an incompatible runtime type rather than throwing.

### Synchronous Surface

The foundation surface is synchronous. In-process caches do not benefit from an asynchronous
API and adding `Task<>`-returning overloads at the foundation would invite implementations
to fake async over synchronous storage. Distributed caches that need an asynchronous shape
layer it on top of their package's own contract; the foundation does not prescribe its
shape.

### Entry Lifecycle

`ICache.CreateEntry(key)` returns a disposable, mutable `ICacheEntry`. Callers configure the
entry's `Value`, expiration metadata, eviction callbacks, and priority, then dispose the
entry to commit. Disposing without setting `Value` commits an entry whose stored value is
`null`. Calling `Dispose` a second time on an already-committed entry is a no-op.

### Expiration Semantics

- `AbsoluteExpiration` - explicit UTC timestamp.
- `AbsoluteExpirationRelativeToNow` - offset evaluated at commit time. Must be greater
  than `TimeSpan.Zero`.
- `SlidingExpiration` - idle window. Must be greater than `TimeSpan.Zero`.
- When both absolute and relative are set, the earlier of the two wins.
- Sliding never extends an entry past its absolute deadline.
- Expiration causes a post-eviction callback to fire with reason `Expired`.

### Token-driven Invalidation

`ICacheEntry.ExpirationTokens` accepts any `IChangeToken` (the Cohesion-native interface
from `Assimalign.Cohesion.Core`). When a token fires, the implementation evicts the entry
with reason `TokenExpired`. Subscriptions are disposed by the implementation during
eviction. Tokens that fire synchronously while the implementation is still subscribing
(before the entry is added to its storage) may be lost; callers that need eager detection
should test the token before configuring the entry.

### Eviction Reasons

Reported through `PostEvictionDelegate(key, value, reason, state)`:

- `Removed` - explicit `Remove(key)` or `Clear()`.
- `Replaced` - a subsequent `Set` for the same key.
- `Expired` - absolute or sliding expiration.
- `TokenExpired` - one of the entry's expiration tokens fired.
- `Capacity` - the cache had to release space.

### Priorities

`CacheEntryPriority` ordering is `Low < Normal < High < NeverRemove`. Capacity-driven
eviction visits lower priorities first; entries marked `NeverRemove` are exempt from
capacity eviction but are still removed by explicit `Remove`, expiration, or token
invalidation.

### Concurrency

Implementations must be thread-safe. Reads must be lock-free (or behave equivalently) so
hot read paths scale. Writes synchronize through whatever primitive the implementation
prefers; the contract requires that `Set`-and-then-`TryGetValue` from another thread
observe a consistent state.

### Disposal

`ICache.Dispose` evicts every entry with reason `Removed` and fires their post-eviction
callbacks before returning. Calling `Dispose` more than once is a no-op. After disposal,
all other `ICache` operations throw `CacheException` with `ErrorCode == Disposed`.

## Compliance

There is no external specification for this contract. Implementations are validated by:

1. `CacheContractComplianceTests` in `Assimalign.Cohesion.Caching.InMemory.Tests`. This
   suite is parameterized over a factory so additional implementations can plug in.
2. The lifecycle tests in `MemoryCacheLifecycleTests` lock in expiration, eviction, and
   invalidation semantics.
3. The performance baselines in `MemoryCachePerformanceBaselineTests` ensure operations
   stay within a generous budget so accidental hot-path regressions trip CI.

## Layout Example

```text
Assimalign.Cohesion.Caching/
  src/
    Assimalign.Cohesion.Caching.csproj
    Abstractions/
      ICache.cs
      IMemoryCache.cs
      IDistributedCache.cs
      ICacheEntry.cs
    Exceptions/
      CacheErrorCode.cs
      CacheException.cs
    Extensions/
      CacheExtensions.cs
    CacheEntryPriority.cs
    CacheEvictionReason.cs
    PostEvictionDelegate.cs
    PostEvictionCallbackRegistration.cs
  tests/
    CacheEnumsTests.cs
    CacheExceptionTests.cs
    CacheExtensionsTests.cs
    PostEvictionCallbackRegistrationTests.cs
  docs/
    OVERVIEW.md
    DESIGN.md
```

## Example: Configure an entry with expiration and a callback

```csharp
using var cache = new MemoryCache();

using (var entry = cache.CreateEntry("session:42"))
{
    entry.Value = sessionPayload;
    entry.AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(20);
    entry.SlidingExpiration = TimeSpan.FromMinutes(5);
    entry.Priority = CacheEntryPriority.High;
    entry.PostEvictionCallbacks.Add(new PostEvictionCallbackRegistration(
        (key, value, reason, _) => logger.LogInformation(
            "Session {Key} evicted because {Reason}.", key, reason)));
}

if (cache.TryGetValue<SessionPayload>("session:42", out var session))
{
    return session;
}
```

## Example: Token-driven invalidation

```csharp
using var cache = new MemoryCache();
var token = new ConfigurationChangeToken();

cache.Set("feature-flags", flags, token);

// Later, when the configuration changes:
token.Notify(); // cache entry is evicted with reason TokenExpired.
```
