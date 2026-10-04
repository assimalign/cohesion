using System;
using System.Threading;
using System.Threading.Tasks;
using Assimalign.Cohesion.Database.Transactions;

namespace Assimalign.Cohesion.Database.Graph.Internal;

/// <summary>
/// An explicit graph transaction. A statement that fails inside it aborts the whole transaction
/// (#1188): the engine rolls its work back at once, and the transaction stays the session's
/// transaction, reporting <see cref="TransactionState.Faulted"/>, until the caller ends it.
/// Until then the session refuses every statement with <c>COHDBG007</c>; a rollback ends it, and
/// a commit ends it with <c>COHDBG007</c> and commits nothing. A commit or rollback that started
/// always ends the context: the transaction kernel completes a started rollback whatever fails
/// or is canceled, and aborts a commit it cannot complete (#1226). Graph DESIGN.md, "Failed
/// statements in explicit transactions", records the contract and the reference engines it
/// follows.
/// </summary>
internal sealed class GraphDatabaseTransaction : IDatabaseTransaction
{
    private readonly TransactionCoordinator _coordinator;
    private readonly ITransactionContext _context;
    private readonly object _sync = new();

    // Serializes every path that ends the context (commit, rollback, dispose, abort), so two of
    // them never race into the coordinator and each one sees the outcome of the one before it.
    private readonly SemaphoreSlim _endGate = new(1, 1);
    private Exception? _failure;
    private bool _ended;

    // The database whose offline state (#1243) refuses the transaction's commit and rollback and
    // makes its disposal touch nothing; null for a transaction composed without one.
    private readonly GraphDatabaseInstance? _database;

    /// <summary>Initializes a new instance of the <see cref="GraphDatabaseTransaction"/> class.</summary>
    /// <param name="coordinator">The transaction coordinator that commits or rolls back the transaction.</param>
    /// <param name="context">The transaction context the transaction wraps.</param>
    /// <param name="database">The database the transaction runs on, whose offline state it observes.</param>
    public GraphDatabaseTransaction(TransactionCoordinator coordinator, ITransactionContext context, GraphDatabaseInstance? database = null)
    {
        _database = database;
        _coordinator = coordinator;
        _context = context;
    }

    internal ITransactionContext Context => _context;
    internal int Operations { get; set; }

    /// <summary>
    /// Gets whether the transaction is still the session's transaction: the caller has not ended
    /// it, or the caller's commit or rollback is still running.
    /// </summary>
    internal bool IsOpen
    {
        get
        {
            lock (_sync)
            {
                return !_ended || _context.State == TransactionState.Active;
            }
        }
    }

    /// <summary>Gets whether statements may run in the transaction: it is open, active and not aborted.</summary>
    internal bool IsUsable
    {
        get
        {
            lock (_sync)
            {
                return !_ended && _failure is null && _context.State == TransactionState.Active;
            }
        }
    }

    public TransactionId Id => _context.Id;

    public TransactionState State
    {
        get
        {
            lock (_sync)
            {
                var state = _context.State;
                if (state != TransactionState.Active)
                {
                    // The kernel ended the context under the caller: Faulted until the caller ends it.
                    return _ended ? state : TransactionState.Faulted;
                }
                // An active context refuses work once a statement failed in it; only a rollback
                // can end it then. A commit or rollback still in flight reports Active.
                return _failure is not null ? TransactionState.Faulted : state;
            }
        }
    }

    public IsolationLevel IsolationLevel => _context.IsolationLevel;

    /// <summary>
    /// Commits the transaction, or, when a statement aborted it, completes its rollback and fails
    /// with <c>COHDBG007</c>. The token is observed only until the commit starts; a token canceled
    /// by then leaves the transaction as it was.
    /// </summary>
    /// <param name="cancellationToken">Cancels the commit before it starts.</param>
    public async ValueTask CommitAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        // An offline database refuses the commit before it starts (#1243): nothing is written,
        // and the reopen's recovery aborts the transaction, which has no commit record.
        _database?.ThrowIfOffline();
        await _endGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Exception? failure;
            bool aborted;
            lock (_sync)
            {
                var state = _context.State;
                if (_ended && state != TransactionState.Active)
                {
                    throw new DatabaseException($"The transaction is {state}.");
                }
                failure = _failure;

                // Under the end gate an ended transaction whose context is still active had a
                // rollback that threw before the kernel ended it. The kernel ends every started
                // rollback (#1226), but refuses one before it starts while the database closes
                // (the manager is disposed: every end it refuses then is an ObjectDisposedException,
                // because its disposal flags itself before it claims any end); nothing the caller rolled
                // back may commit then.
                aborted = failure is not null || state != TransactionState.Active || _ended;
                if (!aborted && Operations != 0)
                {
                    throw new DatabaseException("Dispose every graph operation before committing its transaction.");
                }
                // COMMIT ends the transaction whatever its outcome, as a failed transaction block's
                // COMMIT does in PostgreSQL and a terminated transaction's commit does in Neo4j.
                _ended = true;
            }
            if (!aborted)
            {
                try
                {
                    // Like a rollback, a commit that started runs to completion: a cancellation here
                    // would only turn into a kernel abort of work the caller asked to keep.
                    await _coordinator.CommitAsync(_context).ConfigureAwait(false);
                }
                catch (Exception error)
                {
                    // A kernel abort of the commit crosses the engine boundary as the area root's exception.
                    var translated = Translate(error);
                    if (ReferenceEquals(translated, error)) { throw; }
                    throw translated;
                }
                return;
            }
            // A statement failed: nothing may commit. The abort's own rollback normally ran already;
            // this one completes it when the commit took the end gate first.
            await RollbackContextAsync().ConfigureAwait(false);
            throw CreateAbortedException(failure, commit: true);
        }
        finally
        {
            _endGate.Release();
        }
    }

    /// <summary>
    /// Rolls the transaction back. A transaction that did not commit accepts any number of
    /// rollbacks, because its work is already undone once one has completed or the kernel aborted
    /// it; a committed transaction refuses one, because nothing can undo its work. The token is
    /// observed only until the rollback starts: a rollback that stopped half way would leave the
    /// transaction holding the database writer lock.
    /// </summary>
    /// <param name="cancellationToken">Cancels the rollback before it starts.</param>
    public async ValueTask RollbackAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        // An offline database refuses the rollback too (#1243): its undo could write nothing,
        // and the reopen's recovery aborts the transaction.
        _database?.ThrowIfOffline();
        await _endGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            lock (_sync)
            {
                if (_context.State == TransactionState.Committed)
                {
                    throw new DatabaseException("The transaction is Committed; a committed transaction cannot roll back.");
                }
                _ended = true;
            }
            await RollbackContextAsync().ConfigureAwait(false);
        }
        finally
        {
            _endGate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _endGate.WaitAsync().ConfigureAwait(false);
        try
        {
            lock (_sync)
            {
                _ended = true;
            }
            await RollbackContextAsync().ConfigureAwait(false);
        }
        finally
        {
            _endGate.Release();
        }
    }

    /// <summary>
    /// Aborts the transaction because a statement failed in it: records the first failure, then
    /// rolls the transaction's work back so it holds no writer lock while it waits for the
    /// caller's rollback. The failure is recorded first, so the session refuses later statements
    /// even when the rollback itself fails.
    /// </summary>
    /// <param name="cause">The failure the caller observed from the statement.</param>
    internal async ValueTask AbortAsync(Exception cause)
    {
        lock (_sync)
        {
            if (!_ended)
            {
                _failure ??= cause;
            }
        }
        await _endGate.WaitAsync().ConfigureAwait(false);
        try
        {
            await RollbackContextAsync().ConfigureAwait(false);
        }
        finally
        {
            _endGate.Release();
        }
    }

    /// <summary>
    /// Creates the error for a statement or BEGIN the session refuses because the transaction is
    /// not usable: <c>COHDBG007</c> when it is aborted, or a plain error while the caller's own
    /// commit or rollback is still in flight.
    /// </summary>
    /// <returns>The refusal.</returns>
    internal DatabaseException CreateRefusal()
    {
        lock (_sync)
        {
            if (_failure is not null)
            {
                return CreateAbortedException(_failure);
            }
            if (_ended && _context.State == TransactionState.Active)
            {
                return new DatabaseException("The session's transaction is being committed or rolled back; start the statement after it ends.");
            }
            return CreateAbortedException(null);
        }
    }

    /// <summary>Creates the <c>COHDBG007</c> error for work refused by an aborted transaction.</summary>
    /// <param name="failure">The failure that aborted the transaction, or null when it ended for another reason.</param>
    /// <param name="commit">True when the refused work is the transaction's commit.</param>
    /// <returns>The coded error, carrying <paramref name="failure"/> as its inner exception.</returns>
    internal static DatabaseException CreateAbortedException(Exception? failure, bool commit = false)
    {
        // The cause travels in the message as well: the wire carries message text, not inner exceptions.
        string cause = failure is null ? string.Empty : " Cause: " + failure.Message;
        string message = commit
            ? "COHDBG007: The session's transaction is aborted and cannot commit; nothing was committed." + cause
            : "COHDBG007: The session's transaction is aborted; statements are refused until it is rolled back." + cause;
        return new DatabaseException(message, failure);
    }

    // Runs under the end gate. A rollback that started runs to completion: the caller's token is
    // not passed on (PostgreSQL holds interrupts through AbortTransaction for the same reason), and
    // the kernel ends the context whatever fails once it starts (#1226). Only a closing database
    // refuses the rollback before it starts, and its disposal then aborts the context itself.
    private async ValueTask RollbackContextAsync()
    {
        // On an offline database the disposal, the session's teardown and a statement's abort
        // touch nothing (#1243): the reopen's recovery aborts the transaction.
        if (_context.State != TransactionState.Active || _database?.IsOffline == true)
        {
            return;
        }
        try
        {
            await _coordinator.RollbackAsync(_context).ConfigureAwait(false);
        }
        catch (Exception error)
        {
            var translated = Translate(error);
            if (ReferenceEquals(translated, error)) { throw; }
            throw translated;
        }
    }

    // A failure the offline storage caused is the database's coded refusal (#1243), checked first:
    // the offline error is a StorageException, which the kernel translation reports as COHDBG006.
    private Exception Translate(Exception error)
    {
        var offline = _database?.TranslateOffline(error) ?? error;
        return ReferenceEquals(offline, error) ? GraphDatabaseInstance.TranslateKernelFailure(error) : offline;
    }
}
