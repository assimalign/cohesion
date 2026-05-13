namespace Assimalign.Cohesion.Caching;

/// <summary>
/// Diagnostics codes attached to <see cref="CacheException"/>.
/// </summary>
public enum CacheErrorCode
{
    /// <summary>
    /// Unclassified error.
    /// </summary>
    Unknown = 0,

    /// <summary>
    /// The cache has been disposed and can no longer be used.
    /// </summary>
    Disposed = 1,

    /// <summary>
    /// A configured entry could not be committed because it failed validation
    /// (for example, a non-positive expiration window or a negative size).
    /// </summary>
    InvalidEntry = 2,

    /// <summary>
    /// The cache rejected the entry because committing it would exceed the configured
    /// capacity even after eviction.
    /// </summary>
    CapacityExceeded = 3,
}
