using System;

using Assimalign.Cohesion.Database.Transactions.Internal;

namespace Assimalign.Cohesion.Database.Transactions;

/// <summary>
/// Creates <see cref="ITransactionManager"/> instances. One manager serves one
/// logical database; engines compose it from a transaction log (the write-ahead
/// binding), a lock manager, and a version store.
/// </summary>
public static class TransactionManager
{
    /// <summary>
    /// Creates a transaction manager over the specified collaborators.
    /// </summary>
    /// <param name="log">The transaction log enforcing the write-ahead rule.</param>
    /// <param name="lockManager">The lock manager arbitrating write-write conflicts.</param>
    /// <param name="versionStore">The version store rows/documents resolve through.</param>
    /// <param name="sequenceAllocator">
    /// An optional external sequence allocator invoked (under the manager's begin
    /// lock) to assign each transaction's <see cref="TransactionSequence"/>. An
    /// engine that pairs manager transactions with storage-level transactions
    /// supplies the storage's allocator here so both layers share one monotonic
    /// sequence space — the journal then carries a single sequence namespace and
    /// recovery classification cannot be poisoned by cross-space collisions. When
    /// null, the manager assigns from its own private counter.
    /// </param>
    /// <returns>The transaction manager.</returns>
    /// <remarks>
    /// A rollback whose undo fails (<see cref="IVersionStore.PurgeWriterAsync"/> throws)
    /// still ends its transaction, but the writer stays in the active table and keeps its
    /// locks until its undo completes, because its versions are still in the store (#1226).
    /// A manager created here has no version-purge pass to retry that undo: only its
    /// disposal does. Until then every writer that conflicts with the rolled-back one
    /// waits, and <see cref="ITransactionManager.OldestActive"/> stays at or below it.
    /// <see cref="TransactionCoordinator"/>, which composes a manager over a database's
    /// storage, retries the undo on every <see cref="TransactionCoordinator.RunVersionPurgePass"/>.
    /// </remarks>
    public static ITransactionManager Create(
        ITransactionLog log,
        ILockManager lockManager,
        IVersionStore versionStore,
        Func<TransactionSequence>? sequenceAllocator = null)
    {
        ArgumentNullException.ThrowIfNull(log);
        ArgumentNullException.ThrowIfNull(lockManager);
        ArgumentNullException.ThrowIfNull(versionStore);

        return new DefaultTransactionManager(log, lockManager, versionStore, sequenceAllocator);
    }
}
