using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Database.KeyValuePair;

/// <summary>
/// Represents a key-value-model database: an ordered key space with point and
/// range operations and per-entry etags for conditional writes.
/// </summary>
/// <remarks>
/// Keys and values are opaque byte sequences; keys order by unsigned
/// lexicographic byte comparison, making prefix and range scans meaningful. The
/// typed members are conveniences over the session's typed-request seam — each
/// executes the corresponding <see cref="KeyValueRequest"/> on the given session,
/// so visibility and conflict semantics are identical to executing the request
/// directly. Conditional misses (compare-and-swap) are first-class outcomes;
/// concurrency conflicts surface as the root's retryable transaction exceptions
/// (<see cref="DatabaseTransactionAbortedException"/> /
/// <see cref="DatabaseTransactionDeadlockException"/>).
/// </remarks>
public interface IKeyValueDatabase : IDatabase
{
    /// <summary>
    /// Reads the entry for a key.
    /// </summary>
    /// <param name="session">The session the read executes in.</param>
    /// <param name="key">The key to read.</param>
    /// <param name="cancellationToken">Cancellation token for the operation.</param>
    /// <returns>The entry, or null when the key has no visible, live entry.</returns>
    /// <exception cref="DatabaseException">Thrown when the session does not belong to this database.</exception>
    ValueTask<KeyValueEntry?> GetAsync(IDatabaseSession session, ReadOnlyMemory<byte> key, CancellationToken cancellationToken = default);

    /// <summary>
    /// Writes the entry for a key, inserting or replacing, optionally conditional.
    /// </summary>
    /// <param name="session">The session the write executes in.</param>
    /// <param name="key">The key to write.</param>
    /// <param name="value">The value to store.</param>
    /// <param name="options">Write conditions (insert-only, compare-and-swap), or null for an unconditional upsert.</param>
    /// <param name="cancellationToken">Cancellation token for the operation.</param>
    /// <returns>The write outcome: whether it applied, and the new (or current) etag.</returns>
    /// <exception cref="DatabaseException">Thrown when the session does not belong to this database.</exception>
    /// <exception cref="DatabaseTransactionAbortedException">Thrown when a concurrently committed transaction changed the key (first-updater-wins; retryable).</exception>
    ValueTask<KeyValuePutResult> PutAsync(IDatabaseSession session, ReadOnlyMemory<byte> key, ReadOnlyMemory<byte> value, KeyValuePutOptions? options = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes the entry for a key, optionally conditional.
    /// </summary>
    /// <param name="session">The session the delete executes in.</param>
    /// <param name="key">The key to delete.</param>
    /// <param name="expectedETag">The etag the current entry must carry for the delete to apply, or null for an unconditional delete.</param>
    /// <param name="cancellationToken">Cancellation token for the operation.</param>
    /// <returns>True when an entry was deleted; false when none was visible or the condition did not hold.</returns>
    /// <exception cref="DatabaseException">Thrown when the session does not belong to this database.</exception>
    /// <exception cref="DatabaseTransactionAbortedException">Thrown when a concurrently committed transaction changed the key (first-updater-wins; retryable).</exception>
    ValueTask<bool> TryDeleteAsync(IDatabaseSession session, ReadOnlyMemory<byte> key, long? expectedETag = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Probes whether a key has a visible, live entry.
    /// </summary>
    /// <param name="session">The session the probe executes in.</param>
    /// <param name="key">The key to probe.</param>
    /// <param name="cancellationToken">Cancellation token for the operation.</param>
    /// <returns>True when the key has a visible entry; otherwise false.</returns>
    /// <exception cref="DatabaseException">Thrown when the session does not belong to this database.</exception>
    ValueTask<bool> ExistsAsync(IDatabaseSession session, ReadOnlyMemory<byte> key, CancellationToken cancellationToken = default);

    /// <summary>
    /// Streams entries in ascending key order.
    /// </summary>
    /// <param name="session">The session the scan executes in.</param>
    /// <param name="options">Scan bounds and limits, or null to scan everything.</param>
    /// <param name="cancellationToken">Cancellation token for the operation.</param>
    /// <returns>An async sequence of visible, live entries in ascending key order.</returns>
    /// <exception cref="DatabaseException">Thrown when the session does not belong to this database.</exception>
    IAsyncEnumerable<KeyValueEntry> ScanAsync(IDatabaseSession session, KeyValueScanOptions? options = null, CancellationToken cancellationToken = default);
}
