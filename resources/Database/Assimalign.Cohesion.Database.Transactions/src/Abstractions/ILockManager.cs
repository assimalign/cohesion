using System;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Database.Transactions;

/// <summary>
/// Grants and releases hierarchical locks for write-write conflict control.
/// </summary>
/// <remarks>
/// MVCC keeps readers lock-free; the lock manager arbitrates writers. Locks are
/// owned by transaction sequences and are released as a set at commit or rollback.
/// Implementations detect deadlocks (wait-for graph or timeout policy) and resolve
/// them by aborting a victim with <see cref="TransactionDeadlockException"/>.
/// </remarks>
public interface ILockManager
{
    /// <summary>
    /// Acquires a lock on the specified resource for the specified transaction,
    /// waiting until the lock is granted, the wait times out, or a deadlock is resolved.
    /// </summary>
    /// <param name="owner">The transaction requesting the lock.</param>
    /// <param name="resource">The resource to lock.</param>
    /// <param name="mode">The requested lock mode.</param>
    /// <param name="cancellationToken">Cancellation token for the operation.</param>
    /// <exception cref="TransactionDeadlockException">Thrown when the request was chosen as a deadlock victim.</exception>
    ValueTask AcquireAsync(TransactionSequence owner, LockResource resource, LockMode mode, CancellationToken cancellationToken = default);

    /// <summary>
    /// Attempts to acquire a lock without waiting.
    /// </summary>
    /// <param name="owner">The transaction requesting the lock.</param>
    /// <param name="resource">The resource to lock.</param>
    /// <param name="mode">The requested lock mode.</param>
    /// <returns>True when the lock was granted immediately; otherwise false.</returns>
    bool TryAcquire(TransactionSequence owner, LockResource resource, LockMode mode);

    /// <summary>
    /// Releases every lock held by the specified transaction.
    /// </summary>
    /// <param name="owner">The transaction whose locks are released.</param>
    void ReleaseAll(TransactionSequence owner);
}
