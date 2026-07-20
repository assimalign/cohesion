using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Caching;
using Assimalign.Cohesion.Caching.InMemory;

namespace Assimalign.Cohesion.Web.Caching;

/// <summary>
/// The default in-process <see cref="IOutputCacheStore"/>, adapting the synchronous
/// <see cref="MemoryCache"/> from <c>Assimalign.Cohesion.Caching.InMemory</c>. It honors a cumulative
/// <see cref="MemoryCacheOptions.SizeLimit"/> with per-entry size accounting (each stored entry declares
/// its <see cref="OutputCacheEntry.Size"/>) and maintains its own tag index so tagged responses can be
/// purged in bulk.
/// </summary>
/// <remarks>
/// <para>
/// The store is safe to construct and hold in application code: an application that wants to invalidate
/// cached responses by tag can pass the same instance to <c>UseOutputCache(store, …)</c> and later call
/// <see cref="EvictByTagAsync"/> on it directly (the per-request <see cref="IOutputCacheFeature"/> wraps
/// this same surface for handlers).
/// </para>
/// <para>
/// <b>Time-to-live.</b> Entries expire by absolute time-to-live (<see cref="OutputCacheEntry.ValidFor"/>),
/// not a sliding window, so a cached response is served only while genuinely fresh. The
/// <see cref="TimeProvider"/> supplied at construction drives both the cache's expiration clock and the
/// middleware's <c>Age</c> arithmetic, keeping them on one clock for deterministic tests.
/// </para>
/// <para>
/// <b>Tag index.</b> A tag maps to the set of keys stored under it; <see cref="EvictByTagAsync"/> removes
/// each. A post-eviction callback prunes a key from its tags whenever the cache drops it (expiry,
/// capacity, replacement, or explicit removal), so the index self-cleans and never serves an evicted key.
/// </para>
/// </remarks>
public sealed class InMemoryOutputCacheStore : IOutputCacheStore, IDisposable
{
    private readonly MemoryCache _cache;
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, byte>> _tagIndex = new(StringComparer.Ordinal);

    /// <summary>
    /// Initializes a new store with a default cumulative size limit (64&#8239;MiB) and the system clock.
    /// </summary>
    public InMemoryOutputCacheStore()
        : this(DefaultSizeLimit, timeProvider: null)
    {
    }

    /// <summary>
    /// Initializes a new store with the supplied cumulative size limit and clock.
    /// </summary>
    /// <param name="sizeLimit">The cumulative byte budget across all stored entries. Must be positive.</param>
    /// <param name="timeProvider">The clock used for expiration; <see langword="null"/> uses <see cref="System.TimeProvider.System"/>.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="sizeLimit"/> is not positive.</exception>
    public InMemoryOutputCacheStore(long sizeLimit, TimeProvider? timeProvider = null)
    {
        if (sizeLimit <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(sizeLimit), sizeLimit, "The size limit must be positive.");
        }

        TimeProvider = timeProvider ?? TimeProvider.System;
        _cache = new MemoryCache(new MemoryCacheOptions
        {
            SizeLimit = sizeLimit,
            TimeProvider = TimeProvider,
        });
    }

    /// <summary>
    /// Gets the default cumulative size limit (64&#8239;MiB) applied when none is supplied.
    /// </summary>
    public static long DefaultSizeLimit => 64L * 1024 * 1024;

    /// <summary>
    /// Gets the clock the store uses for expiration and the middleware shares for <c>Age</c> arithmetic.
    /// </summary>
    public TimeProvider TimeProvider { get; }

    /// <inheritdoc />
    public ValueTask<OutputCacheEntry?> GetAsync(string key, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);

        if (_cache.TryGetValue(key, out object? value) && value is OutputCacheEntry entry)
        {
            return new ValueTask<OutputCacheEntry?>(entry);
        }

        return new ValueTask<OutputCacheEntry?>((OutputCacheEntry?)null);
    }

    /// <inheritdoc />
    public ValueTask SetAsync(string key, OutputCacheEntry entry, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        ArgumentNullException.ThrowIfNull(entry);

        try
        {
            using (ICacheEntry cacheEntry = _cache.CreateEntry(key))
            {
                cacheEntry.Value = entry;
                cacheEntry.Size = entry.Size;
                cacheEntry.AbsoluteExpirationRelativeToNow = entry.ValidFor;
                cacheEntry.PostEvictionCallbacks.Add(new PostEvictionCallbackRegistration(PruneTagsCallback, this));
            }
            // Committed on dispose above: a replaced prior entry has already fired its own prune
            // callback, so registering this entry's tags afterward cannot be undone by that eviction.
            IndexTags(key, entry.Tags);
        }
        catch (CacheException)
        {
            // An entry larger than the whole size limit is declined by the foundation with a capacity
            // fault; a store politely drops it rather than surfacing the fault to the request path.
        }

        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public ValueTask EvictByTagAsync(string tag, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(tag);

        if (_tagIndex.TryGetValue(tag, out ConcurrentDictionary<string, byte>? keys))
        {
            // Snapshot the keys: Remove fires the prune callback, which mutates the tag set.
            foreach (string key in keys.Keys.ToArray())
            {
                _cache.Remove(key);
            }
        }

        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// Releases the underlying cache and clears the tag index.
    /// </summary>
    public void Dispose()
    {
        _cache.Dispose();
        _tagIndex.Clear();
    }

    private void IndexTags(string key, IReadOnlyList<string> tags)
    {
        for (int i = 0; i < tags.Count; i++)
        {
            string tag = tags[i];
            if (string.IsNullOrEmpty(tag))
            {
                continue;
            }

            ConcurrentDictionary<string, byte> keys = _tagIndex.GetOrAdd(tag, static _ => new ConcurrentDictionary<string, byte>(StringComparer.Ordinal));
            keys.TryAdd(key, 0);
        }
    }

    private static void PruneTagsCallback(object? key, object? value, CacheEvictionReason reason, object? state)
    {
        if (state is not InMemoryOutputCacheStore store || key is not string keyText || value is not OutputCacheEntry entry)
        {
            return;
        }

        IReadOnlyList<string> tags = entry.Tags;
        for (int i = 0; i < tags.Count; i++)
        {
            if (store._tagIndex.TryGetValue(tags[i], out ConcurrentDictionary<string, byte>? keys))
            {
                keys.TryRemove(keyText, out _);
            }
        }
    }
}
