using System;
using System.Threading;
using System.Threading.Tasks;
using Assimalign.Cohesion.Database.Transactions;

namespace Assimalign.Cohesion.Database.Graph.Internal;

internal sealed class GraphOperation
{
    private readonly GraphDatabaseInstance _database;
    private readonly GraphDatabaseSession? _session;
    private readonly GraphDatabaseTransaction? _transaction;
    private readonly TransactionContext _context;
    private readonly SemaphoreSlim _completionGate = new(1, 1);
    private TransactionContext? _snapshotPin;
    private int _finished;
    internal GraphOperation(GraphDatabaseInstance database, GraphDatabaseSession? session, TransactionContext context, GraphDatabaseTransaction? transaction)
    {
        _database = database;
        _session = session;
        Context = context;
        _context = context;
        _transaction = transaction;
        if (transaction is not null)
        {
            transaction.Operations++;
        }
    }
    internal TransactionContext Context { get; private set; }
    internal async ValueTask InitializeAsync(CancellationToken cancellationToken)
    {
        if (_transaction?.IsolationLevel == IsolationLevel.ReadCommitted)
        {
            // Capture the pin before the statement snapshot. An earlier writer
            // may commit while the operation is open, so the refreshing transaction
            // context alone cannot preserve the statement's original horizon.
            _snapshotPin = await _database.Coordinator.BeginAsync(IsolationLevel.Snapshot, cancellationToken).ConfigureAwait(false);
            Context = _context.PinStatementSnapshot();
        }
    }
    internal void EnsureActive()
    {
        _database.ThrowIfDisposed();
        _database.ThrowIfOffline();
        _session?.ThrowIfNotOpen();
        if (Context.State != TransactionState.Active || Volatile.Read(ref _finished) != 0)
        {
            throw new DatabaseException("The graph operation's transaction is no longer active.");
        }
    }
    internal async ValueTask CompleteAsync()
    {
        await _completionGate.WaitAsync().ConfigureAwait(false);
        try
        {
            EnsureActive();
            try
            {
                if (_transaction is null)
                {
                    await _database.Coordinator.CommitAsync(_context).ConfigureAwait(false);
                }
            }
            finally
            {
                await FinishAsync().ConfigureAwait(false);
            }
        }
        finally { _completionGate.Release(); }
    }
    /// <summary>Ends a failed or abandoned operation, rolling back the transaction it ran in.</summary>
    /// <param name="cause">The failure the caller observed, or why the operation was abandoned.</param>
    internal async ValueTask AbortAsync(Exception cause)
    {
        // Session disposal and the failing operation's catch path can both
        // arrive here. Only one path may perform logical rollback and release
        // the snapshot pin; the second observes the ended transaction context.
        await _completionGate.WaitAsync().ConfigureAwait(false);
        try
        {
            try
            {
                if (_transaction is not null)
                {
                    // Graph storage cannot undo one statement of a transaction, so a failed
                    // statement aborts its whole explicit transaction, which records the cause
                    // and refuses later statements until the caller rolls back (#1188).
                    await _transaction.AbortAsync(cause).ConfigureAwait(false);
                }
                else if (Context.State == TransactionState.Active && !_database.IsOffline)
                {
                    // An offline database undoes nothing (#1243): the reopen's recovery aborts
                    // the operation's transaction.
                    await _database.Coordinator.RollbackAsync(_context).ConfigureAwait(false);
                }
            }
            finally
            {
                await FinishAsync().ConfigureAwait(false);
            }
        }
        finally { _completionGate.Release(); }
    }
    private async ValueTask FinishAsync()
    {
        try { await ReleaseSnapshotPinAsync().ConfigureAwait(false); }
        finally { Finish(); }
    }
    private void Finish()
    {
        if (Interlocked.Exchange(ref _finished, 1) != 0)
        {
            return;
        }

        if (_transaction is not null)
        {
            _transaction.Operations--;
        }

        _session?.Untrack(this);
    }

    private async ValueTask ReleaseSnapshotPinAsync()
    {
        var pin = Interlocked.Exchange(ref _snapshotPin, null);
        if (pin?.State == TransactionState.Active && !_database.IsOffline)
        {
            await _database.Coordinator.RollbackAsync(pin).ConfigureAwait(false);
        }
    }
}
