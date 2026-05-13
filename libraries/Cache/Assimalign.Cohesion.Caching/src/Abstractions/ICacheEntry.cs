using System;
using System.Collections.Generic;
using System.Threading;

namespace Assimalign.Cohesion.Caching;

/// <summary>
/// A configurable cache entry that is committed to its owning cache on <see cref="IDisposable.Dispose"/>.
/// </summary>
/// <remarks>
/// <para>
/// Entries are obtained from <see cref="ICache.CreateEntry(object)"/>. Configure the entry's
/// value, expiration metadata, eviction callbacks, and priority, then dispose the entry to
/// commit. Disposing without setting <see cref="Value"/> commits an entry whose stored value
/// is <see langword="null"/>; disposing a second time is a no-op.
/// </para>
/// <para>
/// Implementations are required to allow <c>Dispose</c> to be called from any thread and must
/// raise no exceptions when the owning cache has already been disposed.
/// </para>
/// </remarks>
public interface ICacheEntry : IDisposable
{
    /// <summary>
    /// Gets the non-null cache key.
    /// </summary>
    object Key { get; }

    /// <summary>
    /// Gets or sets the value associated with the entry. May be <see langword="null"/>.
    /// </summary>
    object? Value { get; set; }

    /// <summary>
    /// Gets or sets an absolute expiration timestamp. When non-null and <see cref="AbsoluteExpirationRelativeToNow"/>
    /// is also set, the earlier of the two wins.
    /// </summary>
    DateTimeOffset? AbsoluteExpiration { get; set; }

    /// <summary>
    /// Gets or sets an absolute expiration offset evaluated against the time the entry is committed.
    /// </summary>
    /// <remarks>
    /// Must be greater than <see cref="TimeSpan.Zero"/> when set.
    /// </remarks>
    TimeSpan? AbsoluteExpirationRelativeToNow { get; set; }

    /// <summary>
    /// Gets or sets the period an entry may remain unaccessed before it is evicted. The sliding
    /// window resets on every successful read.
    /// </summary>
    /// <remarks>
    /// Sliding expiration never extends the entry past <see cref="AbsoluteExpiration"/> or
    /// <see cref="AbsoluteExpirationRelativeToNow"/>; whichever expiration fires first wins.
    /// </remarks>
    TimeSpan? SlidingExpiration { get; set; }

    /// <summary>
    /// Change tokens that, when notified, evict the entry with reason
    /// <see cref="CacheEvictionReason.TokenExpired"/>.
    /// </summary>
    IList<IChangeToken> ExpirationTokens { get; }

    /// <summary>
    /// Callbacks invoked after the entry is evicted from the cache.
    /// </summary>
    IList<PostEvictionCallbackRegistration> PostEvictionCallbacks { get; }

    /// <summary>
    /// Gets or sets the eviction priority. Defaults to <see cref="CacheEntryPriority.Normal"/>.
    /// </summary>
    CacheEntryPriority Priority { get; set; }

    /// <summary>
    /// Gets or sets the entry's logical size. Implementations that enforce a size limit use this
    /// value to decide whether the entry fits and to drive priority-based eviction.
    /// </summary>
    /// <remarks>
    /// Must be greater than or equal to zero when set.
    /// </remarks>
    long? Size { get; set; }
}
