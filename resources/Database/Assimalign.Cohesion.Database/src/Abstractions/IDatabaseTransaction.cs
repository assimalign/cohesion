using System;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Database.Transactions;

namespace Assimalign.Cohesion.Database;

/// <summary>
/// Represents an explicit ACID transaction scope within a database session.
/// </summary>
/// <remarks>
/// A transaction guarantees atomicity and durability for all operations performed
/// between <see cref="IDatabaseSession.BeginTransactionAsync"/> and
/// <see cref="CommitAsync"/> or <see cref="RollbackAsync"/>.
/// Disposing an uncommitted transaction automatically rolls it back.
/// </remarks>
public interface IDatabaseTransaction : IAsyncDisposable
{
    /// <summary>
    /// Gets the unique identifier for this transaction.
    /// </summary>
    TransactionId Id { get; }

    /// <summary>
    /// Gets the current state of the transaction.
    /// </summary>
    TransactionState State { get; }

    /// <summary>
    /// Gets the isolation level this transaction was begun at
    /// (<see cref="IDatabaseSession.BeginTransactionAsync(IsolationLevel, CancellationToken)"/>;
    /// <see cref="IsolationLevel.Snapshot"/> when begun without one). An engine may
    /// execute at a stronger level than requested, never weaker.
    /// </summary>
    IsolationLevel IsolationLevel { get; }

    /// <summary>
    /// Commits all operations performed within this transaction.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token for the operation.</param>
    /// <exception cref="DatabaseException">Thrown when the transaction is not in an active state.</exception>
    /// <exception cref="DatabaseTransactionAbortedException">
    /// The engine aborted the transaction instead of committing it; nothing was committed.
    /// </exception>
    /// <exception cref="DatabaseTransactionCommitUnconfirmedException">
    /// The commit record was written but could not be made durable: the transaction is
    /// committed, and only its durability is unconfirmed. Do not retry its work.
    /// </exception>
    /// <exception cref="ObjectDisposedException">
    /// The database is closed or closing; nothing was committed, and the close aborts the
    /// transaction.
    /// </exception>
    ValueTask CommitAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Rolls back all operations performed within this transaction.
    /// </summary>
    /// <param name="cancellationToken">
    /// Observed only before the rollback starts. A token canceled by then leaves the
    /// transaction as it was.
    /// </param>
    /// <remarks>
    /// A started rollback runs to completion and ends the transaction whatever fails or is
    /// canceled; it throws nothing. When the engine cannot undo the transaction's writes at
    /// once, the transaction still ends, and its locks stay held until the engine completes
    /// the undo. Before it starts, a rollback fails when the transaction cannot be rolled
    /// back in its current state or when its commit or rollback is already running.
    /// </remarks>
    /// <exception cref="OperationCanceledException">The token was canceled before the rollback started.</exception>
    /// <exception cref="DatabaseException">The rollback was refused before it started.</exception>
    /// <exception cref="ObjectDisposedException">
    /// The database is closed or closing, so the rollback did not start; the close aborts the
    /// transaction itself.
    /// </exception>
    ValueTask RollbackAsync(CancellationToken cancellationToken = default);
}
