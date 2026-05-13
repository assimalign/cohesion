namespace Assimalign.Cohesion.Caching;

/// <summary>
/// Reasons reported to post-eviction callbacks when a cache entry is removed.
/// </summary>
public enum CacheEvictionReason
{
    /// <summary>
    /// The entry has not been evicted.
    /// </summary>
    None = 0,

    /// <summary>
    /// The entry was removed explicitly through <see cref="ICache.Remove(object)"/> or
    /// <see cref="ICache.Clear"/>.
    /// </summary>
    Removed = 1,

    /// <summary>
    /// The entry was replaced because a new value was committed for the same key.
    /// </summary>
    Replaced = 2,

    /// <summary>
    /// The entry exceeded its absolute or sliding expiration window.
    /// </summary>
    Expired = 3,

    /// <summary>
    /// One of the entry's expiration tokens fired.
    /// </summary>
    TokenExpired = 4,

    /// <summary>
    /// The entry was evicted because the cache had to release space.
    /// </summary>
    Capacity = 5,
}
