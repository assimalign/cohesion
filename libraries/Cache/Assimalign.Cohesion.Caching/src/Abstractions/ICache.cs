using System;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Caching;

/// <summary>
/// Represents the shared contract for a Cohesion cache.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="ICache"/> is intentionally non-generic on the key and value: cache keys are
/// frequently heterogeneous (strings, composite tuples, runtime tokens) and values are
/// commonly stored as <see cref="object"/> at the storage layer. Strongly typed access is
/// provided by the extension methods on <see cref="CacheExtensions"/>.
/// </para>
/// <para>
/// Implementations are expected to be thread-safe. The contract is synchronous to keep the
/// foundation simple for in-process caches. Distributed cache implementations may layer
/// their own asynchronous surface on top of this contract.
/// </para>
/// </remarks>
public interface ICache : IDisposable
{
    /// <summary>
    /// Creates a new <see cref="ICacheEntry"/> for <paramref name="key"/>.
    /// </summary>
    /// <param name="key">The non-null cache key.</param>
    /// <returns>
    /// A configurable entry. The entry is committed to the cache when it is disposed; if the
    /// caller does not dispose the entry, the cache is left unchanged.
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="key"/> is <see langword="null"/>.</exception>
    ICacheEntry CreateEntry(object key);

    /// <summary>
    /// Attempts to retrieve the cached value for <paramref name="key"/>.
    /// </summary>
    /// <param name="key">The non-null cache key.</param>
    /// <param name="value">When the call returns <see langword="true"/>, the cached value. Otherwise <see langword="null"/>.</param>
    /// <returns><see langword="true"/> when an entry exists and has not yet been evicted; otherwise <see langword="false"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="key"/> is <see langword="null"/>.</exception>
    bool TryGetValue(object key, out object? value);

    /// <summary>
    /// Removes the cache entry associated with <paramref name="key"/> if it exists.
    /// </summary>
    /// <param name="key">The non-null cache key.</param>
    /// <exception cref="ArgumentNullException"><paramref name="key"/> is <see langword="null"/>.</exception>
    void Remove(object key);

    /// <summary>
    /// Removes every cache entry. Post-eviction callbacks fire with
    /// <see cref="CacheEvictionReason.Removed"/>.
    /// </summary>
    void Clear();
}
