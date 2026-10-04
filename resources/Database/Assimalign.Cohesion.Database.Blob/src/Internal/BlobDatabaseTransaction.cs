using System;
using System.Threading;
using System.Threading.Tasks;
using Assimalign.Cohesion.Database.Transactions;

namespace Assimalign.Cohesion.Database.Blob.Internal;

/// <summary>
/// An explicit blob transaction. An operation that fails inside it aborts the whole transaction
/// (#1225, the contract #1188 set for Graph): the engine rolls its work back at once, and the
/// transaction stays the session's transaction, reporting <see cref="TransactionState.Faulted"/>,
/// until the caller ends it. Until then the session refuses every operation and BEGIN with
/// <c>COHDBB001</c>; a rollback ends it, and a commit ends it with <c>COHDBB001</c> and commits
/// nothing. A caller's rollback or commit that does not complete leaves the transaction Faulted in
/// the same way, until a rollback completes. Blob DESIGN.md, "Failed operations in explicit
/// transactions", records the contract and the reference engines it follows.
/// </summary>
internal sealed class BlobDatabaseTransaction : IDatabaseTransaction
{
    private readonly TransactionCoordinator _coordinator;
    private readonly ITransactionContext _context;
    private readonly object _sync = new();

    // Serializes every path that ends the context (commit, rollback, dispose, abort), so two of
    // them never race into the coordinator and each one sees the outcome of the one before it.
    private readonly SemaphoreSlim _endGate = new(1, 1);
    private Exception? _failure;
    private Exception? _endFailure;
    private bool _ended;

    /// <summary>
    /// Initializes a new instance of the <see cref="BlobDatabaseTransaction"/> class.
    /// </summary>
    /// <param name="coordinator">The transaction coordinator that commits and rolls back the transaction.</param>
    /// <param name="context">The transaction context this transaction wraps.</param>
    public BlobDatabaseTransaction(TransactionCoordinator coordinator, ITransactionContext context)
    {
        _coordinator = coordinator;
        _context = context;
    }

    internal ITransactionContext Context => _context;
    internal int Operations { get; set; }

    /// <summary>
    /// Gets whether the transaction is still the session's transaction: the caller has not ended
    /// it, or its rollback has not completed.
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

    /// <summary>Gets whether operations may run in the transaction: it is open, active and not aborted.</summary>
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
                // An active context refuses work once an operation failed in it, or once the
                // caller's commit or rollback did not complete; only a rollback can end it then.
                // A commit or rollback still in flight reports Active.
                return _failure is not null || _endFailure is not null ? TransactionState.Faulted : state;
            }
        }
    }

    public IsolationLevel IsolationLevel => _context.IsolationLevel;

    /// <summary>
    /// Commits the transaction, or, when an operation aborted it or a rollback did not complete,
    /// completes its rollback and fails with <c>COHDBB001</c>. The token is observed only until the
    /// commit starts; a token canceled by then leaves the transaction as it was.
    /// </summary>
    /// <param name="cancellationToken">Cancels the commit before it starts.</param>
    public async ValueTask CommitAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
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
                    // A transaction an operation aborted keeps reporting why nothing committed, so
                    // the outcome never depends on whether the session's teardown ended it first.
                    throw _failure is not null
                        ? CreateAbortedException(_failure, commit: true)
                        : new DatabaseException($"The transaction is {state}.");
                }
                // Under the end gate an ended transaction whose context is still active is one whose
                // commit or rollback did not complete, so it carries an end failure and is aborted.
                failure = _failure ?? _endFailure;
                aborted = failure is not null || state != TransactionState.Active;
                if (!aborted && Operations != 0)
                {
                    throw new DatabaseException("Dispose every blob stream before committing its transaction.");
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
                    RecordEndFailure(error);
                    var translated = BlobDatabaseInstance.TranslateKernelFailure(error);
                    if (ReferenceEquals(translated, error)) { throw; }
                    throw translated;
                }
                return;
            }
            // Only when the rollback at the failure, or the caller's own, did not complete: nothing may commit.
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
    /// Aborts the transaction because an operation failed in it: records the first failure, then
    /// rolls the transaction's work back so it holds no writer lock while it waits for the
    /// caller's rollback. The failure is recorded first, so the session refuses later operations
    /// even when the rollback itself fails.
    /// </summary>
    /// <param name="cause">The failure the caller observed from the operation.</param>
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
    /// Creates the error for an operation or BEGIN the session refuses because the transaction is
    /// not usable: <c>COHDBB001</c> when it is aborted, or a plain error while the caller's own
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
            if (_endFailure is not null)
            {
                return new DatabaseException(
                    "COHDBB001: The session's transaction is aborted: its commit or rollback did not complete, and operations are " +
                    "refused until RollbackAsync completes. Cause: " + _endFailure.Message, _endFailure);
            }
            if (_ended && _context.State == TransactionState.Active)
            {
                return new DatabaseException("The session's transaction is being committed or rolled back; start the operation after it ends.");
            }
            return CreateAbortedException(null);
        }
    }

    /// <summary>Creates the <c>COHDBB001</c> error for work refused by an aborted transaction.</summary>
    /// <param name="failure">The failure that aborted the transaction, or null when it ended for another reason.</param>
    /// <param name="commit">True when the refused work is the transaction's commit.</param>
    /// <returns>The coded error, carrying <paramref name="failure"/> as its inner exception.</returns>
    internal static DatabaseException CreateAbortedException(Exception? failure, bool commit = false)
    {
        // The cause travels in the message as well, so it survives any boundary that keeps only text.
        string cause = failure is null ? string.Empty : " Cause: " + failure.Message;
        string message = commit
            ? "COHDBB001: The session's transaction is aborted and cannot commit; nothing was committed." + cause
            : "COHDBB001: The session's transaction is aborted; operations are refused until it is rolled back." + cause;
        return new DatabaseException(message, failure);
    }

    // Runs under the end gate. A rollback that started runs to completion: the caller's token is
    // not passed on (PostgreSQL holds interrupts through AbortTransaction for the same reason).
    private async ValueTask RollbackContextAsync()
    {
        if (_context.State != TransactionState.Active)
        {
            return;
        }
        try
        {
            await _coordinator.RollbackAsync(_context).ConfigureAwait(false);
        }
        catch (Exception error)
        {
            RecordEndFailure(error);
            var translated = BlobDatabaseInstance.TranslateKernelFailure(error);
            if (ReferenceEquals(translated, error)) { throw; }
            throw translated;
        }
    }

    // A commit or rollback that failed while the context stayed active leaves a transaction that
    // only a rollback can end; State reports it Faulted and the session refuses operations in it.
    private void RecordEndFailure(Exception error)
    {
        lock (_sync)
        {
            if (_context.State == TransactionState.Active)
            {
                _endFailure = error;
            }
        }
    }
}
