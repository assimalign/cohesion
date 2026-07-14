using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Database.KeyValuePair.Client;

/// <summary>
/// One typed key-value connection: point operations (get/put/delete/exists) and
/// ordered, bounded scans against the bound database, with per-entry etags for
/// conditional writes.
/// </summary>
/// <remarks>
/// Connections are not thread-safe — one command at a time, mirroring the
/// engine-session contract. A connection rented from an
/// <see cref="IKeyValueClient"/> returns to its pool on dispose (with its
/// authenticated session intact when it is still healthy). Conditional misses
/// (compare-and-swap) are first-class outcomes
/// (<see cref="KeyValueWriteResult"/>, a false return), never exceptions;
/// concurrency conflicts with other transactions surface as
/// <see cref="KeyValueClientException"/> with
/// <see cref="KeyValueClientErrorKind.ExecutionFailure"/> (retryable).
/// </remarks>
public interface IKeyValueConnection : IAsyncDisposable
{
    /// <summary>
    /// Gets the database this connection is bound to.
    /// </summary>
    string Database { get; }

    /// <summary>
    /// Gets a value indicating whether the connection is open and usable. A
    /// command-level failure leaves the connection open; a protocol or transport
    /// failure marks it broken.
    /// </summary>
    bool IsOpen { get; }

    /// <summary>
    /// Reads the entry for a key.
    /// </summary>
    /// <param name="key">The key to read.</param>
    /// <param name="cancellationToken">Cancellation token for the operation.</param>
    /// <returns>The entry, or null when the key has no visible entry.</returns>
    /// <exception cref="KeyValueClientException">Thrown when the server reports an error or the connection breaks mid-exchange.</exception>
    ValueTask<KeyValueClientEntry?> GetAsync(ReadOnlyMemory<byte> key, CancellationToken cancellationToken = default);

    /// <summary>
    /// Writes the entry for a key unconditionally, inserting or replacing.
    /// </summary>
    /// <param name="key">The key to write.</param>
    /// <param name="value">The value to store.</param>
    /// <param name="cancellationToken">Cancellation token for the operation.</param>
    /// <returns>The entry's new etag.</returns>
    /// <exception cref="KeyValueClientException">Thrown when the server reports an error (including a retryable write-write conflict) or the connection breaks mid-exchange.</exception>
    ValueTask<long> PutAsync(ReadOnlyMemory<byte> key, ReadOnlyMemory<byte> value, CancellationToken cancellationToken = default);

    /// <summary>
    /// Writes the entry for a key under a condition (insert-only or
    /// compare-and-swap). A conditional miss is a first-class outcome, never an
    /// exception.
    /// </summary>
    /// <param name="key">The key to write.</param>
    /// <param name="value">The value to store.</param>
    /// <param name="condition">The write condition.</param>
    /// <param name="cancellationToken">Cancellation token for the operation.</param>
    /// <returns>The outcome: whether the write applied, and the new (or current) etag.</returns>
    /// <exception cref="KeyValueClientException">Thrown when the server reports an error or the connection breaks mid-exchange.</exception>
    ValueTask<KeyValueWriteResult> PutAsync(ReadOnlyMemory<byte> key, ReadOnlyMemory<byte> value, KeyValueWriteCondition condition, CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes the entry for a key.
    /// </summary>
    /// <param name="key">The key to delete.</param>
    /// <param name="cancellationToken">Cancellation token for the operation.</param>
    /// <returns>True when an entry was deleted; false when none was visible.</returns>
    /// <exception cref="KeyValueClientException">Thrown when the server reports an error or the connection breaks mid-exchange.</exception>
    ValueTask<bool> TryDeleteAsync(ReadOnlyMemory<byte> key, CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes the entry for a key only when its current etag matches
    /// (compare-and-swap). A mismatch is a false return, never an exception.
    /// </summary>
    /// <param name="key">The key to delete.</param>
    /// <param name="expectedETag">The etag the current entry must carry.</param>
    /// <param name="cancellationToken">Cancellation token for the operation.</param>
    /// <returns>True when the entry was deleted; false when none was visible or the etag did not match.</returns>
    /// <exception cref="KeyValueClientException">Thrown when the server reports an error or the connection breaks mid-exchange.</exception>
    ValueTask<bool> TryDeleteAsync(ReadOnlyMemory<byte> key, long expectedETag, CancellationToken cancellationToken = default);

    /// <summary>
    /// Probes whether a key has a visible entry.
    /// </summary>
    /// <param name="key">The key to probe.</param>
    /// <param name="cancellationToken">Cancellation token for the operation.</param>
    /// <returns>True when the key has a visible entry; otherwise false.</returns>
    /// <exception cref="KeyValueClientException">Thrown when the server reports an error or the connection breaks mid-exchange.</exception>
    ValueTask<bool> ExistsAsync(ReadOnlyMemory<byte> key, CancellationToken cancellationToken = default);

    /// <summary>
    /// Scans entries in ascending key order, bounded by the given range.
    /// </summary>
    /// <param name="range">Scan bounds and limits, or null to scan the whole key space.</param>
    /// <param name="cancellationToken">Cancellation token for the operation.</param>
    /// <returns>The matching entries in ascending key order, materialized.</returns>
    /// <exception cref="KeyValueClientException">Thrown when the server reports an error or the connection breaks mid-exchange.</exception>
    ValueTask<IReadOnlyList<KeyValueClientEntry>> ScanAsync(KeyValueScanRange? range = null, CancellationToken cancellationToken = default);
}
