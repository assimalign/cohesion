using System;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Database.Sql.Internal;

using Assimalign.Cohesion.Database.Transactions;

/// <summary>
/// Internal ACID transaction implementation binding the root's
/// <c>IDatabaseTransaction</c> surface to an MVCC transaction context from the
/// database's transaction manager (the engine owns both vocabularies — this is
/// the translation boundary). Commit and rollback flow through the manager,
/// whose journal-bound log owns the commit record and its durability await;
/// rollback undoes the writer's stamps through the version store's ledger and
/// releases its locks.
/// </summary>
internal sealed class SqlDatabaseTransaction : IDatabaseTransaction
{
    private readonly TransactionCoordinator _coordinator;
    private readonly TransactionContext _context;
    private readonly SqlDatabaseInstance? _database;

    internal SqlDatabaseTransaction(TransactionCoordinator coordinator, TransactionContext context, SqlDatabaseInstance? database = null)
    {
        _coordinator = coordinator;
        _context = context;
        _database = database;
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
    internal TransactionContext Context => _context;

    /// <inheritdoc />
    public async ValueTask CommitAsync(CancellationToken cancellationToken = default)
    {
        // The offline refusal comes first (#1243): a transaction of an instance that went offline
        // and was then closed by a reopen reports the coded refusal, not the state its close
        // left it in.
        _database?.ThrowIfOffline();

        if (_context.State != TransactionState.Active)
        {
            throw new DatabaseException($"Cannot commit transaction in state '{_context.State}'.");
        }

        try
        {
            await _coordinator.CommitAsync(_context, cancellationToken).ConfigureAwait(false);
        }
        catch (TransactionCommitUnconfirmedException exception)
        {
            // Committed in this process (the context reports Committed); the failed flush took
            // the database offline, and the reopen's recovery decides whether the commit
            // survives (#1243). Not an abort: the work must not be retried.
            throw new DatabaseTransactionCommitUnconfirmedException(exception.Message, exception);
        }
        catch (Exception exception) when (_database?.TranslateOffline(exception) is { } translated && !ReferenceEquals(translated, exception))
        {
            throw translated;
        }
        catch (TransactionDeadlockException exception)
        {
            throw new DatabaseTransactionDeadlockException(exception.Message, exception);
        }
        catch (TransactionAbortedException exception)
        {
            // The area error policy: the engine translates the transaction
            // kernel's independent exception root at the model boundary.
            throw new DatabaseTransactionAbortedException(exception.Message, exception);
        }
    }

    /// <inheritdoc />
    /// <remarks>
    /// The token is observed only before the rollback starts: a token canceled by
    /// then leaves the transaction active. A started rollback runs to completion
    /// and always ends the transaction (#1226) — a rollback stopped half way would
    /// leave the writer holding its locks.
    /// </remarks>
    public async ValueTask RollbackAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        // An offline database undoes nothing (#1243): the reopen's recovery aborts every
        // transaction without a commit record. The refusal comes before the state check, as in
        // CommitAsync.
        _database?.ThrowIfOffline();

        if (_context.State != TransactionState.Active)
        {
            throw new DatabaseException($"Cannot rollback transaction in state '{_context.State}'.");
        }

        try
        {
            await _coordinator.RollbackAsync(_context, CancellationToken.None).ConfigureAwait(false);
        }
        catch (TransactionAbortedException exception)
        {
            throw new DatabaseTransactionAbortedException(exception.Message, exception);
        }
    }

    /// <inheritdoc />
    /// <remarks>
    /// Disposing an active transaction of an offline database touches nothing and throws
    /// nothing: the database refuses every operation until it is reopened, and the reopen's
    /// recovery aborts the transaction, which has no commit record.
    /// </remarks>
    public async ValueTask DisposeAsync()
    {
        if (_context.State == TransactionState.Active && _database?.IsOffline != true)
        {
            await RollbackAsync().ConfigureAwait(false);
        }
    }
}
