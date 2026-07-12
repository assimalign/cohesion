using System;

namespace Assimalign.Cohesion.Database.Storage;

/// <summary>
/// Represents a storage resource that manages the three core file assets for a database:
/// data pages, a journal (write-ahead log), and backup snapshots.
/// </summary>
/// <remarks>
/// Each logical database managed by an engine gets its own <see cref="IStorage"/> instance
/// with isolated file streams. The page infrastructure (<see cref="BufferPool"/>,
/// <see cref="PageManager"/>, <see cref="FreeSpaceMap"/>) operates exclusively on
/// the <see cref="Data"/> stream.
/// </remarks>
public interface IStorage : IAsyncDisposable, IDisposable
{
    /// <summary>
    /// Gets the unique identifier for this storage resource.
    /// </summary>
    StorageId Id { get; }

    /// <summary>
    /// Gets the name of this storage resource.
    /// </summary>
    Name Name { get; }

    /// <summary>
    /// Gets the storage model implemented within this storage resource.
    /// </summary>
    StorageModel Model { get; }

    /// <summary>
    /// Gets the data stream providing page-level I/O for the <c>.dat</c> file.
    /// </summary>
    /// <remarks>
    /// All page-based operations (record storage, indexes, catalog metadata) are performed
    /// against this stream through the <see cref="PageManager"/>.
    /// </remarks>
    StorageStream Data { get; }

    /// <summary>
    /// Gets the journal stream providing sequential I/O for the <c>.log</c> file.
    /// </summary>
    /// <remarks>
    /// The journal stream backs the write-ahead log that guarantees ACID durability.
    /// Transaction records are appended sequentially and flushed on commit.
    /// </remarks>
    StorageStream Journal { get; }

    /// <summary>
    /// Gets the backup stream for the <c>.bak</c> file.
    /// </summary>
    /// <remarks>
    /// Used for point-in-time backup snapshots. The backup stream is separate from
    /// the data and journal streams to avoid contention during normal operations.
    /// </remarks>
    StorageStream Backup { get; }

    /// <summary>
    /// Gets the buffer pool that caches pages in memory with pin-counting and LRU eviction.
    /// </summary>
    IStorageBufferPool BufferPool { get; }

    /// <summary>
    /// Gets the page manager that coordinates page allocation, retrieval, and flushing
    /// against the <see cref="Data"/> stream.
    /// </summary>
    IStoragePageManager PageManager { get; }

    /// <summary>
    /// Gets the free space map that tracks allocated and free pages in the data file.
    /// </summary>
    IStorageFreeSpaceMap FreeSpaceMap { get; }

    /// <summary>
    /// Gets an iterator for scanning all storage units (records) across data pages.
    /// </summary>
    /// <remarks>
    /// Best for performing raw full-table scans through the entire storage resource.
    /// </remarks>
    /// <returns>A new storage unit iterator.</returns>
    IStorageUnitIterator GetUnitIterator();

    /// <summary>
    /// Begins a storage-level transaction: the unit of atomicity and durability for
    /// record mutations. See <see cref="IStorageTransaction"/> for the semantics.
    /// </summary>
    /// <returns>The new transaction scope.</returns>
    IStorageTransaction BeginTransaction();

    /// <summary>
    /// Checkpoints the storage: durably flushes all page state to the data stream and
    /// truncates the journal, so the next open recovers instantly.
    /// </summary>
    /// <exception cref="StorageTransactionException">A transaction is still active.</exception>
    void Checkpoint();
}
