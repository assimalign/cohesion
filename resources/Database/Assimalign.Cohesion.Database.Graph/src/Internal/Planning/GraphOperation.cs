using System;
using System.Collections.Generic;
using System.Collections.Frozen;
using Assimalign.Cohesion.Database.Graph.Language;
using System.Threading;
using System.Threading.Tasks;
using Assimalign.Cohesion.Database.Transactions;

namespace Assimalign.Cohesion.Database.Graph.Internal;

/// <summary>
/// One statement of a session: its transaction context (the explicit transaction's, or an
/// autocommit context of its own), and the hold it keeps on the session and its admission into
/// the explicit transaction until it completes or is aborted.
/// </summary>
[global::System.Diagnostics.DebuggerDisplay("GraphOperation")]
[global::System.Diagnostics.DebuggerTypeProxy(typeof(global::Assimalign.Cohesion.Database.Graph.Internal.GraphOperation.DebugView))]
internal sealed class GraphOperation
{
    internal IReadOnlyDictionary<GqlNamedFunctionCallExpression, GraphFunction> FunctionBindings { get; set; } = FrozenDictionary<GqlNamedFunctionCallExpression, GraphFunction>.Empty;
    internal CancellationToken FunctionCancellation { get; set; }
    internal GraphPlanExecutor.ExpansionBudget? QueryBudget { get; set; }
    private readonly GraphDatabase _database;
    private readonly GraphDatabaseSession _session;
    private readonly GraphDatabaseTransaction? _transaction;
    private readonly TransactionContext _context;
    private readonly SemaphoreSlim _completionGate = new(1, 1);
    private TransactionContext? _snapshotPin;
    private int _finished;
    private GraphMergeStatement? _merge;
    internal GraphMergeStatement Merge => _merge ??= new(_database, this, _transaction?.MergeLocks);

    /// <summary>Initializes a new instance of the <see cref="GraphOperation"/> class.</summary>
    /// <param name="database">The database the statement runs on.</param>
    /// <param name="session">The session the statement holds; the operation releases the hold when it finishes.</param>
    /// <param name="context">The transaction context the statement runs under.</param>
    /// <param name="transaction">The explicit transaction that admitted the statement, or null for autocommit.</param>
    /// <param name="transactionStart">The UTC instant captured before the transaction began.</param>
    internal GraphOperation(GraphDatabase database, GraphDatabaseSession session, TransactionContext context, GraphDatabaseTransaction? transaction, DateTimeOffset transactionStart)
    {
        _database = database;
        _session = session;
        Context = context;
        _context = context;
        _transaction = transaction;
        TransactionStart = transactionStart;
        Updates = new GraphUpdates(database, this);
    }

    internal TransactionContext Context { get; private set; }
    internal DateTimeOffset TransactionStart { get; }
    internal GraphEvaluationContext EvaluationContext => new(TransactionStart, _database, this);
    internal GraphUpdates Updates { get; }

    internal async ValueTask InitializeAsync(CancellationToken cancellationToken)
    {
        if (_context.IsolationLevel == IsolationLevel.ReadCommitted)
        {
            // Capture the pin before the statement snapshot. An earlier writer
            // may commit while the operation is open, so the refreshing transaction
            // context alone cannot preserve the statement's original horizon.
            _snapshotPin = await _database.Coordinator.BeginSnapshotPinAsync(cancellationToken).ConfigureAwait(false);
            Context = _context.PinStatementSnapshot();
        }
    }

    internal ValueTask RefreshWriterSnapshotAsync(CancellationToken cancellationToken)
    {
        if (_context.IsolationLevel == IsolationLevel.ReadCommitted && _merge?.SnapshotCaptured != true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            // The existing pin's horizon is at or below the refreshed view's,
            // so it still protects every version this statement can read.
            Context = _context.PinStatementSnapshot();
        }
        return ValueTask.CompletedTask;
    }

    internal void EnsureActive()
    {
        _database.EnsureNotDisposed();
        _database.ThrowIfOffline();
        _session.ThrowIfNotOpen();
        if (Context.State != TransactionState.Active || Volatile.Read(ref _finished) != 0)
        {
            throw DatabaseException.CreateGraphFailure("CK04002", ErrorMessages.GraphOperationEnsureActiveDatabase);
        }
    }

    internal async ValueTask CompleteAsync()
    {
        await _completionGate.WaitAsync().ConfigureAwait(false);
        try
        {
            EnsureActive();
            bool committed = false;
            try
            {
                if (_transaction is null)
                {
                    await _database.Coordinator.CommitAsync(_context).ConfigureAwait(false);
                    committed = true;
                }
            }
            finally
            {
                try { await FinishAsync().ConfigureAwait(false); }
                catch (Exception error) when (committed && error is not OutOfMemoryException)
                {
                    // The commit is durable. Cleanup cannot turn it into a failed statement
                    // that a caller might retry and apply twice.
                    GraphDatabaseEventSource.Log.StatementCleanupFailed(_database.Name, error);
                }
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
                    await _transaction.AbortForFailedStatementAsync(cause).ConfigureAwait(false);
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
        finally
        {
            _merge?.Dispose();
            Finish();
        }
    }

    private void Finish()
    {
        if (Interlocked.Exchange(ref _finished, 1) != 0)
        {
            return;
        }

        // The statement ends its admission into the explicit transaction and its hold on the session.
        _session.ReleaseOperation(_transaction);
    }

    private async ValueTask ReleaseSnapshotPinAsync()
    {
        var pin = Interlocked.Exchange(ref _snapshotPin, null);
        if (pin?.State == TransactionState.Active && !_database.IsOffline)
        {
            _database.BeforeSnapshotPinRelease?.Invoke(pin);
            await _database.Coordinator.RollbackAsync(pin).ConfigureAwait(false);
        }
    }

    private sealed class DebugView
    {
        public DebugView(global::Assimalign.Cohesion.Database.Graph.Internal.GraphOperation value)
        {
            _ = value;
        }
    }
}
