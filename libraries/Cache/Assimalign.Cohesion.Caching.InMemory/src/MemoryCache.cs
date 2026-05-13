using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using Assimalign.Cohesion.Caching.InMemory.Internal;

namespace Assimalign.Cohesion.Caching.InMemory;

/// <summary>
/// In-process implementation of <see cref="ICache"/>.
/// </summary>
/// <remarks>
/// <para>
/// The cache stores entries in a <see cref="ConcurrentDictionary{TKey,TValue}"/> keyed by the
/// caller-supplied entry key. Reads are lock-free; writes synchronize through the dictionary's
/// own atomic primitives. Expiration is enforced lazily on access and during background scans
/// triggered no more often than <see cref="MemoryCacheOptions.ExpirationScanFrequency"/>.
/// </para>
/// <para>
/// When <see cref="MemoryCacheOptions.SizeLimit"/> is set, every committed entry must declare
/// a size. Capacity-driven eviction picks lower-priority entries first and falls back to
/// least-recently-accessed entries within the same priority bucket. Entries with
/// <see cref="CacheEntryPriority.NeverRemove"/> are exempt from capacity-driven eviction but
/// still respect explicit removal, expiration, and token-driven invalidation.
/// </para>
/// </remarks>
public sealed class MemoryCache : ICache
{
    private readonly ConcurrentDictionary<object, StoredEntry> _entries = new();
    private readonly MemoryCacheOptions _options;
    private readonly TimeProvider _timeProvider;
    private long _totalSize;
    private long _lastScanTicks;
    private int _disposed;

    /// <summary>
    /// Initializes a new cache with default options.
    /// </summary>
    public MemoryCache()
        : this(new MemoryCacheOptions())
    {
    }

    /// <summary>
    /// Initializes a new cache with the supplied options.
    /// </summary>
    /// <param name="options">Configuration knobs for the cache. Required.</param>
    /// <exception cref="ArgumentNullException"><paramref name="options"/> is <see langword="null"/>.</exception>
    public MemoryCache(MemoryCacheOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        _options = options;
        _timeProvider = options.TimeProvider ?? TimeProvider.System;
        _lastScanTicks = _timeProvider.GetUtcNow().UtcTicks;
    }

    /// <summary>
    /// Gets the number of entries currently held by the cache.
    /// </summary>
    public int Count => _entries.Count;

    /// <summary>
    /// Gets the cumulative size of every entry currently held by the cache. Always returns zero
    /// when <see cref="MemoryCacheOptions.SizeLimit"/> is not set.
    /// </summary>
    public long TotalSize => Volatile.Read(ref _totalSize);

    /// <inheritdoc />
    public ICacheEntry CreateEntry(object key)
    {
        ArgumentNullException.ThrowIfNull(key);
        ThrowIfDisposed();

        return new MemoryCacheEntry(this, key);
    }

    /// <inheritdoc />
    public bool TryGetValue(object key, out object? value)
    {
        ArgumentNullException.ThrowIfNull(key);
        ThrowIfDisposed();

        ScanIfDue();

        if (_entries.TryGetValue(key, out var stored))
        {
            var now = _timeProvider.GetUtcNow();
            if (stored.IsExpired(now))
            {
                if (TryRemoveStored(stored))
                {
                    stored.TryMarkEvicted(CacheEvictionReason.Expired);
                }

                value = null;
                return false;
            }

            stored.Touch(now);
            value = stored.Value;
            return true;
        }

        value = null;
        return false;
    }

    /// <inheritdoc />
    public void Remove(object key)
    {
        ArgumentNullException.ThrowIfNull(key);
        ThrowIfDisposed();

        if (_entries.TryRemove(key, out var stored))
        {
            DecrementSize(stored);
            stored.TryMarkEvicted(CacheEvictionReason.Removed);
        }
    }

    /// <inheritdoc />
    public void Clear()
    {
        ThrowIfDisposed();

        foreach (var pair in _entries)
        {
            if (_entries.TryRemove(pair))
            {
                DecrementSize(pair.Value);
                pair.Value.TryMarkEvicted(CacheEvictionReason.Removed);
            }
        }
    }

    /// <summary>
    /// Removes any entry whose absolute or sliding expiration has elapsed.
    /// </summary>
    /// <remarks>
    /// Foundation callers do not normally need to invoke this method; expired entries are
    /// removed lazily by other cache operations. It is exposed for diagnostics and tests.
    /// </remarks>
    public void Compact()
    {
        ThrowIfDisposed();
        ScanForExpired(_timeProvider.GetUtcNow());
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        foreach (var pair in _entries)
        {
            if (_entries.TryRemove(pair))
            {
                DecrementSize(pair.Value);
                pair.Value.TryMarkEvicted(CacheEvictionReason.Removed);
            }
        }
    }

    internal void CommitEntry(MemoryCacheEntry entry)
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            // Cache disposed mid-commit: invoke any post-eviction callbacks with Removed and walk away.
            FireOrphanCallbacks(entry, CacheEvictionReason.Removed);
            return;
        }

        var now = _timeProvider.GetUtcNow();
        var absoluteExpiration = ResolveAbsoluteExpiration(entry, now);
        var sliding = entry.SlidingExpiration;

        ValidateEntry(entry, absoluteExpiration, sliding);

        // Validate size before subscribing to tokens so token subscription side effects (callbacks
        // firing synchronously during OnChange, for example) are not surfaced when we already know
        // the entry cannot be admitted.
        if (_options.SizeLimit is { } sizeLimit)
        {
            if (entry.Size is not { } size)
            {
                throw new CacheException(
                    CacheErrorCode.InvalidEntry,
                    "When MemoryCacheOptions.SizeLimit is set every committed entry must declare a Size.");
            }

            if (size > sizeLimit)
            {
                FireOrphanCallbacks(entry, CacheEvictionReason.Capacity);
                throw new CacheException(
                    CacheErrorCode.CapacityExceeded,
                    "The entry's Size exceeds the cache's SizeLimit.");
            }
        }

        IDisposable[] subscriptions = SubscribeTokens(entry);

        var stored = new StoredEntry(
            entry.Key,
            entry.Value,
            absoluteExpiration,
            sliding,
            entry.Priority,
            entry.Size,
            now,
            entry.Callbacks,
            subscriptions);

        // Replace any prior entry atomically.
        StoredEntry? previous = null;
        _entries.AddOrUpdate(
            entry.Key,
            stored,
            (_, existing) =>
            {
                previous = existing;
                return stored;
            });

        if (previous is not null)
        {
            DecrementSize(previous);
            previous.TryMarkEvicted(CacheEvictionReason.Replaced);
        }

        if (entry.Size is { } newSize)
        {
            Interlocked.Add(ref _totalSize, newSize);
            EnforceSizeLimit();
        }

        // Cache is closing right after we committed: roll back the entry so disposal stays clean.
        if (Volatile.Read(ref _disposed) != 0 && _entries.TryRemove(entry.Key, out var racing) && ReferenceEquals(racing, stored))
        {
            DecrementSize(stored);
            stored.TryMarkEvicted(CacheEvictionReason.Removed);
        }
    }

    internal void OnTokenExpired(object key, StoredEntry stored)
    {
        if (_entries.TryRemove(new KeyValuePair<object, StoredEntry>(key, stored)))
        {
            DecrementSize(stored);
            stored.TryMarkEvicted(CacheEvictionReason.TokenExpired);
        }
    }

    private void ThrowIfDisposed()
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            throw new CacheException(CacheErrorCode.Disposed, "The MemoryCache has been disposed.");
        }
    }

    private static void ValidateEntry(MemoryCacheEntry entry, DateTimeOffset? absoluteExpiration, TimeSpan? sliding)
    {
        if (entry.AbsoluteExpirationRelativeToNow is { } relative && relative <= TimeSpan.Zero)
        {
            throw new CacheException(
                CacheErrorCode.InvalidEntry,
                "AbsoluteExpirationRelativeToNow must be greater than zero.");
        }

        if (sliding is { } slidingValue && slidingValue <= TimeSpan.Zero)
        {
            throw new CacheException(
                CacheErrorCode.InvalidEntry,
                "SlidingExpiration must be greater than zero.");
        }

        if (entry.Size is { } size && size < 0)
        {
            throw new CacheException(
                CacheErrorCode.InvalidEntry,
                "Size must be greater than or equal to zero.");
        }

        // Absolute expiration in the past is allowed at the entry level (the entry expires
        // immediately on first read) so callers can pre-stage already-expired sentinels.
        _ = absoluteExpiration;
    }

    private static DateTimeOffset? ResolveAbsoluteExpiration(MemoryCacheEntry entry, DateTimeOffset now)
    {
        DateTimeOffset? result = entry.AbsoluteExpiration;

        if (entry.AbsoluteExpirationRelativeToNow is { } relative)
        {
            var candidate = now + relative;
            result = result is null ? candidate : (candidate < result ? candidate : result);
        }

        return result;
    }

    private IDisposable[] SubscribeTokens(MemoryCacheEntry entry)
    {
        var tokens = entry.Tokens;
        if (tokens.Count == 0)
        {
            return [];
        }

        var subscriptions = new IDisposable[tokens.Count];

        for (int i = 0; i < tokens.Count; i++)
        {
            var token = tokens[i];

            // The real stored entry is resolved at notification time through the dictionary so
            // this works even when the entry was replaced between commit and notification. Tokens
            // that fire synchronously during OnChange (for example, already-fired tokens) execute
            // their callback before the entry has been added to the dictionary; in that case the
            // lookup is a miss and the entry is committed unaffected. Callers that need eager
            // detection of an already-fired token should test the token before passing it in.
            subscriptions[i] = token.OnChange(
                static state =>
                {
                    var binding = (TokenBinding)state!;
                    if (binding.Cache._entries.TryGetValue(binding.Key, out var current))
                    {
                        binding.Cache.OnTokenExpired(binding.Key, current);
                    }
                },
                new TokenBinding(this, entry.Key));
        }

        return subscriptions;
    }

    private void DecrementSize(StoredEntry stored)
    {
        if (stored.Size is { } size)
        {
            Interlocked.Add(ref _totalSize, -size);
        }
    }

    private bool TryRemoveStored(StoredEntry stored)
    {
        var pair = new KeyValuePair<object, StoredEntry>(stored.Key, stored);
        if (_entries.TryRemove(pair))
        {
            DecrementSize(stored);
            return true;
        }

        return false;
    }

    private void ScanIfDue()
    {
        var now = _timeProvider.GetUtcNow();
        var last = new DateTimeOffset(Volatile.Read(ref _lastScanTicks), TimeSpan.Zero);
        if (now - last < _options.ExpirationScanFrequency)
        {
            return;
        }

        // Only one scanner at a time; if the swap loses we let the winner do the work.
        if (Interlocked.CompareExchange(ref _lastScanTicks, now.UtcTicks, last.UtcTicks) != last.UtcTicks)
        {
            return;
        }

        ScanForExpired(now);
    }

    private void ScanForExpired(DateTimeOffset now)
    {
        foreach (var pair in _entries)
        {
            if (pair.Value.IsExpired(now) && TryRemoveStored(pair.Value))
            {
                pair.Value.TryMarkEvicted(CacheEvictionReason.Expired);
            }
        }
    }

    private void EnforceSizeLimit()
    {
        if (_options.SizeLimit is not { } limit)
        {
            return;
        }

        if (Volatile.Read(ref _totalSize) <= limit)
        {
            return;
        }

        var target = limit - (long)(limit * _options.CompactionPercentage);
        if (target < 0)
        {
            target = 0;
        }

        // Walk eligible entries ordered by priority (Low first) then by least-recently-used.
        // We use a small staging list and sort it rather than maintaining an auxiliary structure
        // because in-memory caches with a size limit are expected to be small enough that this is
        // cheap, and the alternative would add per-access write contention.
        var candidates = new List<StoredEntry>(_entries.Count);
        foreach (var pair in _entries)
        {
            if (pair.Value.Priority == CacheEntryPriority.NeverRemove)
            {
                continue;
            }

            candidates.Add(pair.Value);
        }

        candidates.Sort(static (a, b) =>
        {
            var byPriority = ((int)a.Priority).CompareTo((int)b.Priority);
            if (byPriority != 0)
            {
                return byPriority;
            }

            return a.LastAccessed.CompareTo(b.LastAccessed);
        });

        for (int i = 0; i < candidates.Count; i++)
        {
            if (Volatile.Read(ref _totalSize) <= target)
            {
                return;
            }

            var candidate = candidates[i];
            if (TryRemoveStored(candidate))
            {
                candidate.TryMarkEvicted(CacheEvictionReason.Capacity);
            }
        }
    }

    private static void FireOrphanCallbacks(MemoryCacheEntry entry, CacheEvictionReason reason)
    {
        var callbacks = entry.Callbacks;
        for (int i = 0; i < callbacks.Count; i++)
        {
            var registration = callbacks[i];

            try
            {
                registration.EvictionCallback(entry.Key, entry.Value, reason, registration.State);
            }
            catch
            {
                // Callbacks are best-effort; they may not abort the orphaned commit path.
            }
        }
    }

    private sealed class TokenBinding
    {
        public TokenBinding(MemoryCache cache, object key)
        {
            Cache = cache;
            Key = key;
        }

        public MemoryCache Cache { get; }

        public object Key { get; }
    }
}
