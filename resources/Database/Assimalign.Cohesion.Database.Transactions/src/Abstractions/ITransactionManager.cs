using System;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Database.Transactions;

/// <summary>
/// Coordinates the transaction lifecycle for a database engine: sequence assignment,
/// snapshot capture, commit ordering, and durable commit through the transaction log.
/// </summary>
/// <remarks>
/// One manager instance serves one logical database. Engines create sessions whose
/// <see cref="IDatabaseTransaction"/> surfaces delegate to this manager. The manager
/// owns the active-transaction table used to capture <see cref="TransactionSnapshot"/>
/// instances and drives write-ahead logging through <see cref="ITransactionLog"/>.
/// </remarks>
public interface ITransactionManager : IAsyncDisposable
{
    /// <summary>
    /// Gets the oldest transaction sequence still active, below which every
    /// version is decided and version chains may be pruned.
    /// </summary>
    TransactionSequence OldestActive { get; }

    /// <summary>
    /// Begins a new transaction at the specified isolation level.
    /// </summary>
    /// <param name="isolationLevel">The isolation level for the transaction.</param>
    /// <param name="cancellationToken">Cancellation token for the operation.</param>
    /// <returns>The context for the new transaction.</returns>
    ValueTask<ITransactionContext> BeginAsync(IsolationLevel isolationLevel = IsolationLevel.Snapshot, CancellationToken cancellationToken = default);

    /// <summary>
    /// Durably commits the specified transaction. Returns only after the commit
    /// record is durable per the engine's durability policy.
    /// </summary>
    /// <param name="context">The transaction to commit.</param>
    /// <param name="cancellationToken">Cancellation token for the operation.</param>
    /// <exception cref="TransactionAbortedException">Thrown when the transaction was aborted by conflict or deadlock resolution.</exception>
    ValueTask CommitAsync(ITransactionContext context, CancellationToken cancellationToken = default);

    /// <summary>
    /// Rolls back the specified transaction, undoing its effects.
    /// </summary>
    /// <param name="context">The transaction to roll back.</param>
    /// <param name="cancellationToken">Cancellation token for the operation.</param>
    ValueTask RollbackAsync(ITransactionContext context, CancellationToken cancellationToken = default);
}
