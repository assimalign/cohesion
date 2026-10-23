using System;
using System.Collections.Generic;
using System.Threading;

namespace Assimalign.Cohesion.Caching.InMemory.Internal;

/// <summary>
/// Mutable scratch space returned from <see cref="MemoryCache.CreateEntry(object)"/>.
/// </summary>
/// <remarks>
/// <para>
/// The entry stays detached from the cache until <see cref="Dispose"/> is called. On dispose the
/// entry is validated and committed atomically; a second dispose is a no-op.
/// </para>
/// <para>
/// The entry is intentionally not thread-safe by itself: it is configured by the caller that
/// obtained it from <see cref="MemoryCache.CreateEntry(object)"/>, then committed once. Concurrent
/// configuration of the same entry from multiple threads is a usage error.
/// </para>
/// </remarks>
[global::System.Diagnostics.DebuggerDisplay("MemoryCacheEntry")]
[global::System.Diagnostics.DebuggerTypeProxy(typeof(DebugView))]
internal sealed class MemoryCacheEntry : ICacheEntry
{
    private readonly MemoryCache _cache;
    private List<IChangeToken>? _expirationTokens;
    private List<PostEvictionCallbackRegistration>? _postEvictionCallbacks;
    private int _committed;

    public MemoryCacheEntry(MemoryCache cache, object key)
    {
        _cache = cache;
        Key = key;
    }

    public object Key { get; }

    public object? Value { get; set; }

    public DateTimeOffset? AbsoluteExpiration { get; set; }

    public TimeSpan? AbsoluteExpirationRelativeToNow { get; set; }

    public TimeSpan? SlidingExpiration { get; set; }

    public IList<IChangeToken> ExpirationTokens => _expirationTokens ??= [];

    public IList<PostEvictionCallbackRegistration> PostEvictionCallbacks => _postEvictionCallbacks ??= [];

    public CacheEntryPriority Priority { get; set; } = CacheEntryPriority.Normal;

    public long? Size { get; set; }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _committed, 1) != 0)
        {
            return;
        }

        _cache.CommitEntry(this);
    }

    internal IReadOnlyList<IChangeToken> Tokens => _expirationTokens is null ? Array.Empty<IChangeToken>() : _expirationTokens;

    internal IReadOnlyList<PostEvictionCallbackRegistration> Callbacks => _postEvictionCallbacks is null
        ? Array.Empty<PostEvictionCallbackRegistration>()
        : _postEvictionCallbacks;

    private sealed class DebugView
    {
        private readonly MemoryCacheEntry _target;

        public DebugView(MemoryCacheEntry value)
        {
            _target = value;
        }

        public object Key => _target.Key;
        public object? Value => _target.Value;
        public DateTimeOffset? AbsoluteExpiration => _target.AbsoluteExpiration;
        public TimeSpan? AbsoluteExpirationRelativeToNow => _target.AbsoluteExpirationRelativeToNow;
        public TimeSpan? SlidingExpiration => _target.SlidingExpiration;
        public CacheEntryPriority Priority => _target.Priority;
        public long? Size => _target.Size;
    }
}
