using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Database.KeyValuePair;

/// <summary>
/// Represents a key-value-model database: an ordered key space with point and
/// range operations and optional per-entry expiration.
/// </summary>
/// <remarks>
/// Keys and values are opaque byte sequences; keys order by unsigned lexicographic
/// byte comparison, making prefix and range scans meaningful.
/// </remarks>
public interface IKeyValueDatabase : IDatabase
{
    /// <summary>
    /// Reads the entry for a key.
    /// </summary>
    /// <param name="session">The session the read executes in.</param>
    /// <param name="key">The key to read.</param>
    /// <param name="cancellationToken">Cancellation token for the operation.</param>
    /// <returns>The entry, or null when the key has no visible, unexpired entry.</returns>
    ValueTask<KeyValueEntry?> GetAsync(IDatabaseSession session, ReadOnlyMemory<byte> key, CancellationToken cancellationToken = default);

    /// <summary>
    /// Writes the entry for a key, inserting or replacing.
    /// </summary>
    /// <param name="session">The session the write executes in.</param>
    /// <param name="key">The key to write.</param>
    /// <param name="value">The value to store.</param>
    /// <param name="options">Write options (expiration, insert-only), or null for defaults.</param>
    /// <param name="cancellationToken">Cancellation token for the operation.</param>
    /// <returns>True when the entry was written; false when <see cref="KeyValueSetOptions.OnlyIfAbsent"/> was set and the key already existed.</returns>
    ValueTask<bool> SetAsync(IDatabaseSession session, ReadOnlyMemory<byte> key, ReadOnlyMemory<byte> value, KeyValueSetOptions? options = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes the entry for a key.
    /// </summary>
    /// <param name="session">The session the delete executes in.</param>
    /// <param name="key">The key to delete.</param>
    /// <param name="cancellationToken">Cancellation token for the operation.</param>
    /// <returns>True when an entry was deleted; false when none was visible.</returns>
    ValueTask<bool> DeleteAsync(IDatabaseSession session, ReadOnlyMemory<byte> key, CancellationToken cancellationToken = default);

    /// <summary>
    /// Streams entries in key order.
    /// </summary>
    /// <param name="session">The session the scan executes in.</param>
    /// <param name="options">Scan bounds and limits, or null to scan everything.</param>
    /// <param name="cancellationToken">Cancellation token for the operation.</param>
    /// <returns>An async sequence of visible, unexpired entries in ascending key order.</returns>
    IAsyncEnumerable<KeyValueEntry> ScanAsync(IDatabaseSession session, KeyValueScanOptions? options = null, CancellationToken cancellationToken = default);
}
