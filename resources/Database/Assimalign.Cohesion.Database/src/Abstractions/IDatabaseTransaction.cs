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
    ValueTask CommitAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Rolls back all operations performed within this transaction.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token for the operation.</param>
    ValueTask RollbackAsync(CancellationToken cancellationToken = default);
}
