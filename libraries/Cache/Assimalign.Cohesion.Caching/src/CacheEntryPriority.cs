namespace Assimalign.Cohesion.Caching;

/// <summary>
/// Indicates the relative priority of a cache entry when the owning cache must shed entries
/// to honor a size limit.
/// </summary>
public enum CacheEntryPriority
{
    /// <summary>
    /// Lowest priority. Entries are evicted before <see cref="Normal"/> and <see cref="High"/>.
    /// </summary>
    Low = 0,

    /// <summary>
    /// Default priority.
    /// </summary>
    Normal = 1,

    /// <summary>
    /// Higher than <see cref="Normal"/>. Entries are evicted only after lower-priority entries
    /// have been removed.
    /// </summary>
    High = 2,

    /// <summary>
    /// The entry is exempt from capacity-driven eviction. It is still removed by explicit
    /// <see cref="ICache.Remove(object)"/>, expiration, or token-driven invalidation.
    /// </summary>
    NeverRemove = 3,
}
