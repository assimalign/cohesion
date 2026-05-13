# Assimalign.Cohesion.Caching.InMemory Design

## Storage Layout

Entries are stored in a `ConcurrentDictionary<object, StoredEntry>` keyed by the caller's
cache key. The dictionary handles concurrent read/write coordination internally and avoids a
global lock on the hot read path.

Each `StoredEntry` is immutable for everything that defines the entry (key, value,
expiration deadlines, priority, size, committed timestamp, callbacks, token subscriptions).
The mutable per-entry bits - last access timestamp for sliding expiration and the
"evicted" flag - are tracked with `Volatile.Read` / `Volatile.Write` and `Interlocked`
operations so reads stay lock-free.

## Lifecycle

### Commit

`MemoryCache.CommitEntry(MemoryCacheEntry)` runs the following steps in order:

1. Reject the entry if the cache has been disposed.
2. Resolve the effective absolute expiration by taking the earlier of `AbsoluteExpiration`
   and `now + AbsoluteExpirationRelativeToNow`.
3. Validate fields (`AbsoluteExpirationRelativeToNow > 0`, `SlidingExpiration > 0`,
   `Size >= 0`).
4. If `SizeLimit` is set, require `Size` on the entry and verify it is not larger than the
   limit on its own. An over-sized entry triggers `CapacityExceeded`.
5. Subscribe to each `IChangeToken` in `ExpirationTokens`. The subscription callback
   resolves the entry from the storage dictionary at notification time so it tolerates
   replacement.
6. Build a `StoredEntry` and call `_entries.AddOrUpdate` to install it; any previous entry
   for the same key is recorded for `Replaced` callback dispatch.
7. Decrement the size of the previous entry, add the new entry's size to the running total,
   and invoke `EnforceSizeLimit` if a size limit is set.
8. If the cache was disposed between step 6 and now, roll back the commit so disposal stays
   clean.

### Reads

`TryGetValue` looks up the entry, asks `StoredEntry.IsExpired(now)` whether it is past its
deadline, and either:

- Removes and evicts the entry with reason `Expired`, returning `false`.
- Touches `StoredEntry.LastAccessed = now` to reset the sliding window, returning `true`
  with the stored value.

Every `TryGetValue` also calls `ScanIfDue`, which runs at most once per
`ExpirationScanFrequency` window and walks every entry looking for expirations.

### Eviction

`StoredEntry.TryMarkEvicted(reason)` is the single eviction sink. It is idempotent: it uses
`Interlocked.Exchange` to flip an "evicted" flag exactly once, then disposes the entry's
token subscriptions and walks the post-eviction callbacks. Callback exceptions are
swallowed so one bad callback cannot abort eviction. Token subscription disposal
exceptions are also swallowed for the same reason.

### Capacity compaction

When the running size exceeds the limit, `EnforceSizeLimit` snapshots every non-`NeverRemove`
entry, sorts the snapshot by priority (ascending) and then by `LastAccessed` (ascending), and
walks the snapshot evicting entries with reason `Capacity` until the running size drops to
`SizeLimit * (1 - CompactionPercentage)`.

## Concurrency Model

- **Reads:** `ConcurrentDictionary.TryGetValue` is lock-free. The sliding-window touch is a
  single `Volatile.Write`; concurrent readers can race on the timestamp but always observe a
  monotonically non-decreasing value or a fresh write.
- **Writes:** `ConcurrentDictionary.AddOrUpdate` is the synchronization primitive. Eviction
  uses `TryRemove` with the exact-value overload so concurrent eviction paths cannot
  double-fire callbacks for the same `StoredEntry`.
- **Eviction:** `TryMarkEvicted` is idempotent. The first caller wins; everyone else gets
  `false` and skips the callbacks.
- **Disposal:** `Dispose` flips a flag and walks the dictionary evicting every entry with
  reason `Removed`. Subsequent operations throw `CacheException(Disposed)`. A late commit
  (a `MemoryCacheEntry.Dispose` racing the cache's `Dispose`) rolls itself back if it sees
  the disposed flag after the dictionary update.

## Token Subscription Notes

- Each entry holds an `IDisposable[]` of subscriptions, one per token.
- When the cache evicts the entry, it disposes the subscriptions before firing callbacks.
- A token that fires synchronously during `OnChange` runs before the entry has been added to
  the storage dictionary. In that case the lookup is a miss and the entry is committed
  unaffected. Callers that need eager detection of an already-fired token should test the
  token before configuring the entry.

## Performance Baselines

`MemoryCachePerformanceBaselineTests` runs four scenarios with a generous 10-second budget:

- 100,000 `Set` operations.
- 100,000 `TryGetValue` lookups against a populated cache.
- 50,000 `GetOrCreate` operations against an empty cache (factory always runs).
- `Environment.ProcessorCount * 20,000` parallel `TryGetValue` lookups against a 1,024-entry
  working set.

These thresholds are watchdogs, not microbenchmarks. Hitting them in CI signals that a hot
path has regressed significantly.

## NativeAOT and Trimming

- The implementation uses no reflection.
- `TimeProvider` is the standard CLR abstraction so the cache is trimmable.
- `ConcurrentDictionary<object, StoredEntry>` is AOT-safe.
- Lambda captures in `SubscribeTokens` use static lambdas with a typed binding object so the
  AOT compiler does not have to synthesize a closure type at runtime.

## Compliance

- `CacheContractComplianceTests` exercises every requirement of the `ICache` contract.
- `MemoryCacheLifecycleTests` locks in expiration, eviction, and invalidation semantics.
- `MemoryCacheTests` covers concurrent access, replacement, disposal, and ergonomic helpers.
- `MemoryCacheOptionsTests` covers options validation.
- `MemoryCachePerformanceBaselineTests` watches for hot-path regressions.
