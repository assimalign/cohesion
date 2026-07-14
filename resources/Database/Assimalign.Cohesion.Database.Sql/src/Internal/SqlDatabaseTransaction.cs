using System;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Database.Sql.Internal;

using Assimalign.Cohesion.Database.Storage;
using Assimalign.Cohesion.Database.Transactions;

/// <summary>
/// Internal ACID transaction implementation binding the root's
/// <c>IDatabaseTransaction</c> surface to an MVCC transaction context from the
/// database's transaction manager (the engine owns both vocabularies — this is
/// the translation boundary). Commit and rollback flow through the manager,
/// whose journal-bound log drives the paired storage bracket: commit is
/// acknowledged only after the write-ahead log is durable, rollback restores
/// every modified page to its pre-transaction image and purges the writer from
/// the version store.
/// </summary>
internal sealed class SqlDatabaseTransaction : IDatabaseTransaction
{
    private readonly SqlTransactionCoordinator _coordinator;
    private readonly ITransactionContext _context;

    internal SqlDatabaseTransaction(SqlTransactionCoordinator coordinator, ITransactionContext context)
    {
        _coordinator = coordinator;
        _context = context;
    }

    /// <inheritdoc />
    public TransactionId Id => _context.Id;

    /// <inheritdoc />
    public TransactionState State => _context.State;

    /// <inheritdoc />
    public IsolationLevel IsolationLevel => _context.IsolationLevel;

    /// <summary>
    /// Gets the MVCC transaction context statements execute under: the executor
    /// stamps writes with its sequence and resolves reads through its snapshot
    /// (re-captured per statement under <see cref="IsolationLevel.ReadCommitted"/>,
    /// fixed at begin under <see cref="IsolationLevel.Snapshot"/>).
    /// </summary>
    internal ITransactionContext Context => _context;

    /// <summary>
    /// Gets the storage-level transaction the executor mutates through — the
    /// physical write-ahead bracket paired with <see cref="Context"/> under one
    /// shared sequence.
    /// </summary>
    internal IStorageTransaction StorageTransaction => _coordinator.GetStorageTransaction(_context);

    /// <inheritdoc />
    public async ValueTask CommitAsync(CancellationToken cancellationToken = default)
    {
        if (_context.State != TransactionState.Active)
        {
            throw new DatabaseException($"Cannot commit transaction in state '{_context.State}'.");
        }

        try
        {
            await _coordinator.CommitAsync(_context, cancellationToken).ConfigureAwait(false);
        }
        catch (TransactionAbortedException exception)
        {
            // The area error policy: the engine translates the transaction
            // kernel's independent exception root at the model boundary.
            throw new DatabaseTransactionAbortedException(exception.Message, exception);
        }
    }

    /// <inheritdoc />
    public async ValueTask RollbackAsync(CancellationToken cancellationToken = default)
    {
        if (_context.State != TransactionState.Active)
        {
            throw new DatabaseException($"Cannot rollback transaction in state '{_context.State}'.");
        }

        try
        {
            await _coordinator.RollbackAsync(_context, cancellationToken).ConfigureAwait(false);
        }
        catch (TransactionAbortedException exception)
        {
            throw new DatabaseTransactionAbortedException(exception.Message, exception);
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_context.State == TransactionState.Active)
        {
            await RollbackAsync().ConfigureAwait(false);
        }
    }
}
