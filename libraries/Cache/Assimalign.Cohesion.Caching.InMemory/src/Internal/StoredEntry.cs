using System;
using System.Collections.Generic;
using System.Threading;

namespace Assimalign.Cohesion.Caching.InMemory.Internal;

/// <summary>
/// Immutable view of a committed cache entry kept in the storage dictionary.
/// </summary>
/// <remarks>
/// Mutable per-entry state (last access time for sliding expiration, eviction state, token
/// subscriptions) is tracked here so the public <see cref="ICacheEntry"/> stays a configuration
/// surface only.
/// </remarks>
internal sealed class StoredEntry : IDisposable
{
    private readonly List<PostEvictionCallbackRegistration> _callbacks;
    private readonly IDisposable[] _tokenSubscriptions;
    private long _lastAccessedTicks;
    private int _evicted;

    public StoredEntry(
        object key,
        object? value,
        DateTimeOffset? absoluteExpiration,
        TimeSpan? slidingExpiration,
        CacheEntryPriority priority,
        long? size,
        DateTimeOffset committedAt,
        IReadOnlyList<PostEvictionCallbackRegistration> callbacks,
        IDisposable[] tokenSubscriptions)
    {
        Key = key;
        Value = value;
        AbsoluteExpiration = absoluteExpiration;
        SlidingExpiration = slidingExpiration;
        Priority = priority;
        Size = size;
        CommittedAt = committedAt;
        _callbacks = new List<PostEvictionCallbackRegistration>(callbacks);
        _tokenSubscriptions = tokenSubscriptions;
        _lastAccessedTicks = committedAt.UtcTicks;
    }

    public object Key { get; }

    public object? Value { get; }

    public DateTimeOffset? AbsoluteExpiration { get; }

    public TimeSpan? SlidingExpiration { get; }

    public CacheEntryPriority Priority { get; }

    public long? Size { get; }

    public DateTimeOffset CommittedAt { get; }

    public DateTimeOffset LastAccessed => new(Volatile.Read(ref _lastAccessedTicks), TimeSpan.Zero);

    public bool IsEvicted => Volatile.Read(ref _evicted) != 0;

    public void Touch(DateTimeOffset now) => Volatile.Write(ref _lastAccessedTicks, now.UtcTicks);

    public bool IsExpired(DateTimeOffset now)
    {
        if (AbsoluteExpiration is { } abs && now >= abs)
        {
            return true;
        }

        if (SlidingExpiration is { } sliding && now - LastAccessed >= sliding)
        {
            return true;
        }

        return false;
    }

    /// <summary>
    /// Marks the entry as evicted, releases token subscriptions, and runs registered post-eviction
    /// callbacks. Returns <see langword="true"/> the first time it is called for an entry so the
    /// caller can suppress double-callbacks when multiple eviction paths race.
    /// </summary>
    public bool TryMarkEvicted(CacheEvictionReason reason)
    {
        if (Interlocked.Exchange(ref _evicted, 1) != 0)
        {
            return false;
        }

        // Cancel any token subscriptions so the eviction does not bounce back into the cache.
        for (int i = 0; i < _tokenSubscriptions.Length; i++)
        {
            try
            {
                _tokenSubscriptions[i].Dispose();
            }
            catch
            {
                // Token disposal must not abort eviction.
            }
        }

        for (int i = 0; i < _callbacks.Count; i++)
        {
            var registration = _callbacks[i];

            try
            {
                registration.EvictionCallback(Key, Value, reason, registration.State);
            }
            catch
            {
                // Post-eviction callbacks are best-effort; one bad callback does not abort eviction.
            }
        }

        return true;
    }

    public void Dispose()
    {
        if (Volatile.Read(ref _evicted) == 0)
        {
            for (int i = 0; i < _tokenSubscriptions.Length; i++)
            {
                try
                {
                    _tokenSubscriptions[i].Dispose();
                }
                catch
                {
                }
            }
        }
    }
}
