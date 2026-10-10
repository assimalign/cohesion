using System.Threading;

namespace Assimalign.Cohesion.Database.Storage;

/// <summary>
/// A reader/writer latch over the pages of one structure whose readers take no page write lock,
/// such as a B-tree: readers hold it shared while they read the structure's pages, writers hold it
/// exclusively while they change them, and a storage transaction that changed any of them restores
/// their pre-images under it when it rolls back.
/// </summary>
/// <remarks>
/// <para>
/// A rollback rewrites each page it restores in place: it clears the page and writes the
/// pre-image's runs back. A reader that reads one of those pages without excluding the rollback
/// can see a cleared node, or a parent restored while its child is not yet. So a structure passes
/// its latch to <see cref="Storage.OpenPageForWrite(StorageTransaction, PageId, StoragePageLatch)"/>
/// and <see cref="Storage.AllocatePageForWrite(StorageTransaction, PageType, StoragePageLatch)"/>,
/// which require the latch held exclusively and enlist it with the transaction, and
/// <see cref="StorageTransaction.Rollback"/> holds every latch its transaction enlisted from the
/// first page it restores to the last (#1371).
/// </para>
/// <para>
/// A rollback takes the latches it enlisted in the order they were created, so two rollbacks never
/// wait for each other's latches. A reader holds one latch at a time, and a writer waits for no
/// other latch while it holds one (a page another transaction has write-locked fails the write
/// instead of waiting), so a rollback that waits for a latch waits only for readers and writers
/// that finish without it. The latch does not recurse: a thread that holds it shared may not roll
/// back a transaction that enlisted it.
/// </para>
/// </remarks>
public sealed class StoragePageLatch
{
    private static long s_created;

    private readonly ReaderWriterLockSlim _latch = new(LockRecursionPolicy.NoRecursion);

    private StoragePageLatch()
    {
        Order = Interlocked.Increment(ref s_created);
    }

    /// <summary>
    /// Gets the order a rollback takes this latch in among the others its transaction enlisted:
    /// creation order, unique in the process.
    /// </summary>
    internal long Order { get; }

    /// <summary>
    /// Gets a value indicating whether the current thread holds the latch exclusively.
    /// </summary>
    internal bool IsWriteHeld => _latch.IsWriteLockHeld;

    /// <summary>
    /// Gets the number of threads waiting to hold the latch exclusively (diagnostics and tests).
    /// </summary>
    internal int WaitingWriters => _latch.WaitingWriteCount;

    /// <summary>
    /// Creates a latch for one structure's pages.
    /// </summary>
    /// <returns>The latch.</returns>
    public static StoragePageLatch Create() => new();

    /// <summary>
    /// Holds the latch shared, waiting while a writer or a rollback holds it.
    /// </summary>
    public void EnterRead() => _latch.EnterReadLock();

    /// <summary>
    /// Releases a shared hold.
    /// </summary>
    /// <exception cref="SynchronizationLockException">The current thread does not hold the latch shared.</exception>
    public void ExitRead() => _latch.ExitReadLock();

    /// <summary>
    /// Holds the latch exclusively, waiting for its readers and its writer to leave.
    /// </summary>
    public void EnterWrite() => _latch.EnterWriteLock();

    /// <summary>
    /// Releases an exclusive hold.
    /// </summary>
    /// <exception cref="SynchronizationLockException">The current thread does not hold the latch exclusively.</exception>
    public void ExitWrite() => _latch.ExitWriteLock();
}
