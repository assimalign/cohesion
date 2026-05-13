namespace Assimalign.Cohesion.Caching;

/// <summary>
/// Callback invoked after a cache entry has been evicted.
/// </summary>
/// <param name="key">The key of the evicted entry.</param>
/// <param name="value">The value of the evicted entry, or <see langword="null"/> if no value was ever committed.</param>
/// <param name="reason">The reason the entry was evicted.</param>
/// <param name="state">Caller-supplied state captured when the callback was registered.</param>
public delegate void PostEvictionDelegate(object key, object? value, CacheEvictionReason reason, object? state);
