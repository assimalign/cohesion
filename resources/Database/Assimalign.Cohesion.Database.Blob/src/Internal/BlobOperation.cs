using System;
using System.Threading;
using System.Threading.Tasks;
using Assimalign.Cohesion.Database.Transactions;

namespace Assimalign.Cohesion.Database.Blob.Internal;

internal sealed class BlobOperation
{
    private readonly BlobDatabaseInstance _database;
    private readonly BlobDatabaseSession? _session;
    private readonly BlobDatabaseTransaction? _transaction;
    private readonly ITransactionContext _context;
    private readonly SemaphoreSlim _completionGate = new(1, 1);
    private ITransactionContext? _snapshotPin;
    private int _finished;
    internal BlobOperation(BlobDatabaseInstance database, BlobDatabaseSession? session, ITransactionContext context, BlobDatabaseTransaction? transaction)
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
            // may commit while the stream is open, so the refreshing transaction
            // context alone cannot preserve the statement's original horizon.
            _snapshotPin = await _database.Coordinator.BeginAsync(IsolationLevel.Snapshot, cancellationToken).ConfigureAwait(false);
            Context = new BlobStatementContext(_context, _context.Snapshot);
        }
    }
    internal void EnsureActive()
    {
        _database.ThrowIfDisposed();

        // A blob stream checks here before every read and write, and an upload before its
        // completion: an offline database refuses them with its coded error (#1243).
        _database.ThrowIfOffline();
        _session?.ThrowIfNotOpen();
        if (Context.State != TransactionState.Active || Volatile.Read(ref _finished) != 0)
        {
            throw new DatabaseException("The blob operation's transaction is no longer active.");
        }
    }
    /// <summary>
    /// Translates a failure the operation's stream observed the way every other operation's
    /// failure is translated: the offline storage's coded refusal (#1243), or the area root's
    /// exception for a kernel failure such as the upload's unconfirmed commit.
    /// </summary>
    /// <param name="error">The failure to translate.</param>
    /// <returns>The translated failure, or <paramref name="error"/> itself.</returns>
    internal Exception TranslateFailure(Exception error) => _database.TranslateFailure(error);

    internal async ValueTask CompleteAsync()
    {
        // A stream's disposal, its failure path and session disposal can all
        // arrive at completion or abort; the gate lets exactly one of them end
        // the operation, and the others observe the finished operation.
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
        await _completionGate.WaitAsync().ConfigureAwait(false);
        try
        {
            try
            {
                if (_transaction is not null)
                {
                    // Blob storage cannot undo one operation of a transaction (an upload writes a
                    // physical bracket per chunk), so a failed operation aborts its whole explicit
                    // transaction, which records the cause and refuses later operations until the
                    // caller rolls back (#1225).
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

// One read-committed statement gets one visibility decision, including every
// metadata lookup and all streamed content. Lifecycle operations use the original
// context; physical brackets and record stamps use this identical writer sequence.
internal sealed class BlobStatementContext : ITransactionContext
{
    private readonly ITransactionContext _context;
    private readonly TransactionSnapshot _snapshot;

    /// <summary>
    /// Initializes a new instance of the <see cref="BlobStatementContext"/> class.
    /// </summary>
    /// <param name="context">The original transaction context that supplies identity, sequence, isolation, and state.</param>
    /// <param name="snapshot">The statement snapshot that fixes visibility for the statement.</param>
    public BlobStatementContext(ITransactionContext context, TransactionSnapshot snapshot)
    {
        _context = context;
        _snapshot = snapshot;
    }

    public TransactionId Id => _context.Id;
    public TransactionSequence Sequence => _context.Sequence;
    public IsolationLevel IsolationLevel => _context.IsolationLevel;
    public TransactionState State => _context.State;
    public TransactionSnapshot Snapshot => _snapshot;
}
