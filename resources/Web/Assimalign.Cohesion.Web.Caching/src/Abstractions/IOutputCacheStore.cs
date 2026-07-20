using System;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Web.Caching;

/// <summary>
/// The asynchronous, tag-aware backing-store seam for server-owned output caching. A store keeps
/// opaque <see cref="OutputCacheEntry"/> payloads keyed by string, honoring each entry's own
/// time-to-live and eviction tags, and can purge every entry carrying a given tag. The in-process
/// default is <see cref="InMemoryOutputCacheStore"/>; an out-of-process store (a distributed cache, a
/// key/value resource, a database) implements the same contract so the middleware round-trips
/// identical entries without change.
/// </summary>
/// <remarks>
/// <para>
/// <b>Async by design.</b> Output caching layers this asynchronous surface <em>above</em> the
/// synchronous <c>Assimalign.Cohesion.Caching</c> foundation, per that library's rule that
/// distributed/async shapes belong in the consumer, not the foundation. The in-memory default adapts
/// the synchronous <c>MemoryCache</c> and completes synchronously; a distributed adapter awaits real
/// I/O behind the same signatures.
/// </para>
/// <para>
/// <b>Opaque payloads.</b> The store never inspects a stored entry beyond its <see cref="OutputCacheEntry.Size"/>,
/// <see cref="OutputCacheEntry.ValidFor"/>, and <see cref="OutputCacheEntry.Tags"/>. Response framing
/// (status, headers, body, vary markers) is the middleware's concern; any store that returns the exact
/// entry it was given round-trips a cached response losslessly.
/// </para>
/// <para>
/// <b>Concurrency contract — last-write-wins.</b> <see cref="SetAsync"/> is an unconditional overwrite
/// with no read-modify-write locking. Two concurrent misses for the same key each produce an entry and
/// the later write replaces the earlier wholesale; there is no per-key merge. Tag eviction is atomic
/// per key: an evicted key is gone for subsequent reads.
/// </para>
/// </remarks>
public interface IOutputCacheStore
{
    /// <summary>
    /// Reads the entry stored under <paramref name="key"/>.
    /// </summary>
    /// <param name="key">The cache key to read.</param>
    /// <param name="cancellationToken">A token to cancel the read.</param>
    /// <returns>
    /// The stored entry, or <see langword="null"/> when no live entry exists (never stored, evicted, or
    /// past its time-to-live).
    /// </returns>
    /// <exception cref="ArgumentException"><paramref name="key"/> is <see langword="null"/> or empty.</exception>
    ValueTask<OutputCacheEntry?> GetAsync(string key, CancellationToken cancellationToken = default);

    /// <summary>
    /// Writes (or replaces) the entry stored under <paramref name="key"/>. The write is an unconditional
    /// overwrite; the entry's <see cref="OutputCacheEntry.ValidFor"/> sets its time-to-live and its
    /// <see cref="OutputCacheEntry.Tags"/> index it for <see cref="EvictByTagAsync"/>.
    /// </summary>
    /// <param name="key">The cache key to write.</param>
    /// <param name="entry">The entry to store.</param>
    /// <param name="cancellationToken">A token to cancel the write.</param>
    /// <returns>A task that completes when the entry has been stored (or silently declined when it exceeds the store's own capacity).</returns>
    /// <exception cref="ArgumentException"><paramref name="key"/> is <see langword="null"/> or empty.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="entry"/> is <see langword="null"/>.</exception>
    ValueTask SetAsync(string key, OutputCacheEntry entry, CancellationToken cancellationToken = default);

    /// <summary>
    /// Evicts every entry tagged with <paramref name="tag"/>. A no-op when no entry carries the tag.
    /// </summary>
    /// <param name="tag">The tag whose entries are removed.</param>
    /// <param name="cancellationToken">A token to cancel the eviction.</param>
    /// <returns>A task that completes when the tagged entries have been removed.</returns>
    /// <exception cref="ArgumentException"><paramref name="tag"/> is <see langword="null"/> or empty.</exception>
    ValueTask EvictByTagAsync(string tag, CancellationToken cancellationToken = default);
}
