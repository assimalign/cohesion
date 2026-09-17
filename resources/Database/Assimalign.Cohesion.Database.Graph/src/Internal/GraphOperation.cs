using System.Threading;
using System.Threading.Tasks;
using Assimalign.Cohesion.Database.Transactions;

namespace Assimalign.Cohesion.Database.Graph.Internal;

internal sealed class GraphOperation
{
    private readonly GraphDatabaseInstance _database;
    private readonly GraphDatabaseSession? _session;
    private readonly GraphDatabaseTransaction? _transaction;
    private readonly ITransactionContext _context;
    private readonly SemaphoreSlim _completionGate = new(1, 1);
    private ITransactionContext? _snapshotPin;
    private int _finished;
    internal GraphOperation(GraphDatabaseInstance database, GraphDatabaseSession? session, ITransactionContext context, GraphDatabaseTransaction? transaction)
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
    internal ITransactionContext Context { get; private set; }
    internal async ValueTask InitializeAsync(CancellationToken cancellationToken)
    {
        if (_transaction?.IsolationLevel == IsolationLevel.ReadCommitted)
        {
            // Capture the pin before the statement snapshot. An earlier writer
            // may commit while the operation is open, so the refreshing transaction
            // context alone cannot preserve the statement's original horizon.
            _snapshotPin = await _database.Coordinator.BeginAsync(IsolationLevel.Snapshot, cancellationToken).ConfigureAwait(false);
            Context = new GraphStatementContext(_context, _context.Snapshot);
        }
    }
    internal void EnsureActive()
    {
        _database.ThrowIfDisposed();
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
    internal async ValueTask AbortAsync()
    {
        // Session disposal and the failing operation's catch path can both
        // arrive here. Only one path may perform logical rollback and release
        // the snapshot pin; the second observes the ended transaction context.
        await _completionGate.WaitAsync().ConfigureAwait(false);
        try
        {
            try
            {
                if (Context.State == TransactionState.Active)
                {
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
        if (pin?.State == TransactionState.Active)
        {
            await _database.Coordinator.RollbackAsync(pin).ConfigureAwait(false);
        }
    }
}

// One read-committed statement gets one visibility decision, including every
// metadata lookup and all graph content. Lifecycle operations use the original
// context; physical brackets and record stamps use this identical writer sequence.
internal sealed class GraphStatementContext(ITransactionContext context, TransactionSnapshot snapshot) : ITransactionContext
{
    public TransactionId Id => context.Id;
    public TransactionSequence Sequence => context.Sequence;
    public IsolationLevel IsolationLevel => context.IsolationLevel;
    public TransactionState State => context.State;
    public TransactionSnapshot Snapshot => snapshot;
}
