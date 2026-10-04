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
/// <c>IDatabaseTransaction</c> surfaces delegate to this manager. The manager
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
    /// <exception cref="TransactionAbortedException">
    /// Thrown when the transaction was aborted by conflict or deadlock resolution; when
    /// the commit record could not be written, in which case the transaction was
    /// rolled back and ends as <see cref="TransactionState.Faulted"/>; or, before the
    /// commit starts, when the transaction is not active or its commit or rollback is
    /// already running.
    /// </exception>
    /// <exception cref="TransactionCommitUnconfirmedException">
    /// The commit record was written but could not be made durable. The transaction is
    /// committed: it ends as <see cref="TransactionState.Committed"/>, leaves the active
    /// table and releases its locks. Its commit is lost if the process stops before the log
    /// is next flushed durably.
    /// </exception>
    /// <exception cref="ObjectDisposedException">The manager was disposed.</exception>
    ValueTask CommitAsync(ITransactionContext context, CancellationToken cancellationToken = default);

    /// <summary>
    /// Rolls back the specified transaction, undoing its effects.
    /// </summary>
    /// <param name="context">The transaction to roll back.</param>
    /// <param name="cancellationToken">
    /// Observed only before the rollback starts. A token canceled by then leaves the
    /// transaction active and untouched.
    /// </param>
    /// <remarks>
    /// A started rollback runs to completion, ends the transaction as
    /// <see cref="TransactionState.RolledBack"/> and throws nothing, whatever fails or is
    /// canceled. When the transaction's writes cannot be undone at once, the transaction
    /// still ends, but it keeps its locks, and every snapshot keeps treating it as in
    /// flight, until the manager completes the undo.
    /// </remarks>
    /// <exception cref="OperationCanceledException">The token was canceled before the rollback started.</exception>
    /// <exception cref="TransactionAbortedException">
    /// Thrown before the rollback starts, when the transaction is not active or its
    /// commit or rollback is already running.
    /// </exception>
    /// <exception cref="ObjectDisposedException">The manager was disposed.</exception>
    ValueTask RollbackAsync(ITransactionContext context, CancellationToken cancellationToken = default);
}
