# Assimalign.Cohesion.Caching

## Summary

`Assimalign.Cohesion.Caching` is the cache foundation library for the Cohesion platform. It
defines the contracts every Cohesion cache implementation honors: `ICache`, `ICacheEntry`,
the eviction enums, the post-eviction callback shape, and the diagnostics exception. It
contains no runtime cache itself; implementations live in sibling packages such as
`Assimalign.Cohesion.Caching.InMemory`.

## Status

- Status: Stable foundation.
- Production source files: 9 (2 interfaces, 2 enums, 1 delegate, 1 callback record, 1
  exception, 1 error-code enum, 1 extensions class).
- Project references: `Assimalign.Cohesion.Core` (for `IChangeToken`).
- Package references: None.
- `NotImplementedException` markers: 0.
- Foundation tests: 23, including contract coverage for the eviction primitives, the
  callback registration, the exception type, and every overload of the extension surface.

## Primary Responsibilities

- Define the shared cache surface so consumers and implementations can evolve independently.
- Encapsulate Cohesion-native expiration, eviction, and invalidation primitives without
  pulling in `Microsoft.Extensions.*` types.
- Provide the ergonomic typed access pattern via `CacheExtensions` so implementations stay
  intentionally non-generic on key and value.
- Carry the diagnostics exception (`CacheException`) and error-code enum used by every
  implementation when reporting structural failures.

## Project Boundaries

- The root package owns contracts only. No production cache lives here.
- Implementation packages (`Assimalign.Cohesion.Caching.InMemory`, future
  `Assimalign.Cohesion.Caching.Distributed.*`) reference the root package and add runtime
  behavior on top.
- Implementations may not redefine `ICache`, `ICacheEntry`, or any of the eviction primitives.

## Key Types

- `ICache` - sync entry-CRUD contract with `CreateEntry`, `TryGetValue`, `Remove`, `Clear`.
- `ICacheEntry` - configurable, disposable, commits on `Dispose`.
- `CacheEntryPriority` - `Low`, `Normal`, `High`, `NeverRemove`.
- `CacheEvictionReason` - `None`, `Removed`, `Replaced`, `Expired`, `TokenExpired`, `Capacity`.
- `PostEvictionDelegate` and `PostEvictionCallbackRegistration` - eviction notification surface.
- `CacheException` plus `CacheErrorCode` - diagnostics surface.
- `CacheExtensions` - typed accessors (`Get<T>`, `Set<T>`, `GetOrCreate<T>`, ...).

## Source Layout

- `src/Abstractions` - root contracts (`ICache`, `ICacheEntry`).
- `src/Extensions` - `CacheExtensions`.
- `src/Exceptions` - `CacheException`, `CacheErrorCode`.
- `src/` - eviction primitives (`CacheEntryPriority`, `CacheEvictionReason`,
  `PostEvictionDelegate`, `PostEvictionCallbackRegistration`).
- `src/Properties/AssemblyInfo.cs` - `InternalsVisibleTo` declaration for the test project only.
