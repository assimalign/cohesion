using System;
using System.Collections.Generic;

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
    /// Gets an iterator scoped to one owner's record chain: only data pages tagged
    /// with <paramref name="ownerId"/> are visited, so a scan of one object touches
    /// O(object) pages instead of O(storage). Owner zero iterates the shared,
    /// untagged record space.
    /// </summary>
    /// <param name="ownerId">The owner whose pages to scan.</param>
    /// <returns>A new storage unit iterator over the owner's pages.</returns>
    IStorageUnitIterator GetUnitIterator(ulong ownerId);

    /// <summary>
    /// Gets a point-in-time snapshot of the data pages currently belonging to the
    /// specified owner's record chain, in ascending page order.
    /// </summary>
    /// <param name="ownerId">The owner whose pages to list.</param>
    /// <returns>The owner's data pages; empty when the owner holds none.</returns>
    IReadOnlyList<PageId> GetOwnerPages(ulong ownerId);

    /// <summary>
    /// Releases every data page of the specified owner's record chain inside a
    /// transaction: each page is retyped <see cref="PageType.Free"/> under the
    /// write-ahead log (before-image covered — a rollback restores the chain), and
    /// the pages return to the free-space map when the transaction commits, never
    /// before, so an in-flight release can never be reallocated.
    /// </summary>
    /// <param name="transaction">The owning storage transaction.</param>
    /// <param name="ownerId">The owner whose chain to release.</param>
    /// <returns>The number of pages released.</returns>
    /// <exception cref="StorageTransactionException">The transaction is not active, or a chain page is owned by another transaction.</exception>
    int FreeOwnerPages(IStorageTransaction transaction, ulong ownerId);

    /// <summary>
    /// Begins a storage-level transaction: the unit of atomicity and durability for
    /// record mutations. See <see cref="IStorageTransaction"/> for the semantics.
    /// </summary>
    /// <returns>The new transaction scope.</returns>
    IStorageTransaction BeginTransaction();

    /// <summary>
    /// Reserves the next transaction sequence from this storage's monotonic
    /// sequence space without beginning a transaction. This is the seam an
    /// MVCC transaction manager layered above the storage uses as its sequence
    /// allocator, so logical (manager) and physical (storage) transactions share
    /// one sequence namespace in the journal — a prerequisite for recovery
    /// classification to be collision-free.
    /// </summary>
    /// <returns>The reserved sequence, unique within this storage instance.</returns>
    long ReserveTransactionSequence();

    /// <summary>
    /// Begins a storage-level transaction that adopts a sequence previously
    /// obtained from <see cref="ReserveTransactionSequence"/> — the physical
    /// write-ahead bracket paired with a logical transaction that owns the same
    /// sequence. Unlike <see cref="BeginTransaction()"/>, no begin record is
    /// appended: the reserving caller's transaction log owns the lifecycle
    /// records; the adopted bracket contributes page images and its commit or
    /// rollback record under the shared sequence.
    /// </summary>
    /// <param name="sequence">The reserved sequence the transaction runs under.</param>
    /// <returns>The new transaction scope.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="sequence"/> is not positive.</exception>
    IStorageTransaction BeginTransaction(long sequence);

    /// <summary>
    /// Pins a page for modification inside a transaction: acquires the page's write
    /// lock for the transaction and captures its before image on first touch, so the
    /// mutation is covered by the write-ahead log like any record operation. Used by
    /// subsystems that own their page layout (index structures, catalogs).
    /// </summary>
    /// <param name="transaction">The owning storage transaction.</param>
    /// <param name="pageId">The page to modify.</param>
    /// <returns>A handle to the pinned page; the caller marks it dirty after mutating.</returns>
    /// <exception cref="StorageTransactionException">The transaction is not active, or the page is owned by another transaction.</exception>
    IStoragePageHandle OpenPageForWrite(IStorageTransaction transaction, PageId pageId);

    /// <summary>
    /// Allocates a fresh page inside a transaction, covered by the write-ahead log.
    /// If the transaction rolls back, the page content reverts to its freshly
    /// allocated (empty) image; the allocation itself is not undone — a safe leak.
    /// </summary>
    /// <param name="transaction">The owning storage transaction.</param>
    /// <param name="type">The type of page to allocate.</param>
    /// <returns>A handle to the new pinned page.</returns>
    /// <exception cref="StorageTransactionException">The transaction is not active.</exception>
    IStoragePageHandle AllocatePageForWrite(IStorageTransaction transaction, PageType type);

    /// <summary>
    /// Checkpoints the storage: durably flushes all page state to the data stream and
    /// truncates the journal, so the next open recovers instantly.
    /// </summary>
    /// <exception cref="StorageTransactionException">A transaction is still active.</exception>
    void Checkpoint();

    /// <summary>
    /// Checkpoints the storage while logical (manager-level) transactions are in
    /// flight above it: the truncating checkpoint record carries the given active
    /// sequences, so recovery classification still sees them even though their
    /// begin records were truncated. Storage-level transactions must still be
    /// quiescent — the active-count interlock is unchanged.
    /// </summary>
    /// <param name="activeTransactionSequences">The logical transaction sequences still active above the storage.</param>
    /// <exception cref="StorageTransactionException">A storage-level transaction is still active.</exception>
    void Checkpoint(ReadOnlySpan<long> activeTransactionSequences);

    /// <summary>
    /// Performs one group-commit flush pass on behalf of a write-ahead flush worker:
    /// makes the journal durable up to the highest commit currently waiting on the
    /// grouped durability gate and wakes every covered committer. A no-op when
    /// nothing is pending (including in the synchronous durability mode).
    /// </summary>
    /// <returns>True when a durable flush was performed; false when nothing was pending.</returns>
    bool FlushPendingCommits();

    /// <summary>
    /// Writes back up to <paramref name="maxPages"/> dirty buffered pages to the data
    /// stream — the paced write-back a page-writer worker performs between
    /// checkpoints so a checkpoint's flush does not spike. Honors the write-ahead
    /// rule: the journal is made durable past each page's LSN before the page is
    /// written.
    /// </summary>
    /// <param name="maxPages">The maximum number of dirty pages to write in this pass.</param>
    /// <returns>The number of pages written.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="maxPages"/> is not positive.</exception>
    int WriteBackDirtyPages(int maxPages);
}
