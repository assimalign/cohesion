using System;
using System.Threading;
using System.Threading.Tasks;
using Assimalign.Cohesion.Database.Transactions;

namespace Assimalign.Cohesion.Database.Documents.Internal;

/// <summary>
/// An explicit document transaction. A statement that fails inside it aborts the whole transaction
/// (#1225, the contract #1188 set for Graph): the engine rolls its work back at once, and the
/// transaction stays the session's transaction, reporting <see cref="TransactionState.Faulted"/>,
/// until the caller ends it. Until then the session refuses every statement and BEGIN with
/// <c>COHDBD001</c>; a rollback ends it, and a commit ends it with <c>COHDBD001</c> and commits
/// nothing. A commit or rollback that started always ends the context: the transaction kernel
/// completes a started rollback whatever fails or is canceled, and aborts a commit it cannot
/// complete (#1226). Documents DESIGN.md, "Failed statements in explicit transactions", records the
/// contract and the reference engines it follows.
/// </summary>
internal sealed class DocumentDatabaseTransaction : IDatabaseTransaction
{
    private readonly TransactionCoordinator _coordinator;
    private readonly ITransactionContext _context;
    private readonly object _sync = new();

    // Serializes every path that ends the context (commit, rollback, dispose, abort), so two of
    // them never race into the coordinator and each one sees the outcome of the one before it.
    private readonly SemaphoreSlim _endGate = new(1, 1);
    private Exception? _failure;
    private bool _ended;

    /// <summary>
    /// Initializes a new instance of the <see cref="DocumentDatabaseTransaction"/> class.
    /// </summary>
    /// <param name="coordinator">The transaction coordinator that commits and rolls back the transaction.</param>
    /// <param name="context">The transaction context this transaction wraps.</param>
    public DocumentDatabaseTransaction(TransactionCoordinator coordinator, ITransactionContext context)
    {
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
    /// with <c>COHDBD001</c>. The token is observed only until the commit starts; a token canceled
    /// by then leaves the transaction as it was.
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
                    // A transaction a statement aborted keeps reporting why nothing committed, so
                    // the outcome never depends on whether the session's teardown ended it first.
                    throw _failure is not null
                        ? CreateAbortedException(_failure, commit: true)
                        : new DatabaseException($"The transaction is {state}.");
                }
                failure = _failure;

                // Under the end gate an ended transaction whose context is still active had a
                // commit or rollback that threw before the kernel started it. The kernel ends every
                // started rollback (#1226), but refuses one before it starts while the database
                // closes (disposal claimed the end, or the manager is disposed); nothing the caller
                // rolled back may commit then.
                aborted = failure is not null || state != TransactionState.Active || _ended;
                if (!aborted && Operations != 0)
                {
                    throw new DatabaseException("Dispose every document operation before committing its transaction.");
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
                    // A kernel abort of the commit crosses the engine boundary as the area root's
                    // exception; this one translation serves every end path (commit, rollback,
                    // dispose, close and abort).
                    var translated = DocumentDatabaseInstance.TranslateKernelFailure(error);
                    if (ReferenceEquals(translated, error)) { throw; }
                    throw translated;
                }
                return;
            }
            // A statement failed, or an earlier end was refused before it started: nothing may
            // commit. The abort's own rollback normally ran already; this one completes it when
            // the commit took the end gate first.
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
    /// Ends the transaction because its session closed: rolls it back like a disposal, and records
    /// the closure as the cause when no statement failed first, so a caller that still holds the
    /// transaction gets <c>COHDBD001</c> naming why nothing committed from a later commit. A
    /// transaction its caller already ended keeps its own outcome.
    /// </summary>
    /// <param name="cause">Why the session closed.</param>
    internal async ValueTask CloseAsync(Exception cause)
    {
        await _endGate.WaitAsync().ConfigureAwait(false);
        try
        {
            lock (_sync)
            {
                if (!_ended)
                {
                    _failure ??= cause;
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
    /// not usable: <c>COHDBD001</c> when it is aborted, or a plain error while the caller's own
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

    /// <summary>Creates the <c>COHDBD001</c> error for work refused by an aborted transaction.</summary>
    /// <param name="failure">The failure that aborted the transaction, or null when it ended for another reason.</param>
    /// <param name="commit">True when the refused work is the transaction's commit.</param>
    /// <returns>The coded error, carrying <paramref name="failure"/> as its inner exception.</returns>
    internal static DatabaseException CreateAbortedException(Exception? failure, bool commit = false)
    {
        // The cause travels in the message as well, so it survives any boundary that keeps only text.
        string cause = failure is null ? string.Empty : " Cause: " + failure.Message;
        string message = commit
            ? "COHDBD001: The session's transaction is aborted and cannot commit; nothing was committed." + cause
            : "COHDBD001: The session's transaction is aborted; statements are refused until it is rolled back." + cause;
        return new DatabaseException(message, failure);
    }

    // Runs under the end gate. A rollback that started runs to completion: the caller's token is
    // not passed on (PostgreSQL holds interrupts through AbortTransaction for the same reason), and
    // the kernel ends the context whatever fails once it starts (#1226). Only a closing database
    // refuses the rollback before it starts, and its disposal then aborts the context itself. The
    // kernel's refusal crosses the engine boundary translated (DatabaseTransactionAbortedException),
    // never as the kernel's own exception type.
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
            var translated = DocumentDatabaseInstance.TranslateKernelFailure(error);
            if (ReferenceEquals(translated, error)) { throw; }
            throw translated;
        }
    }
}
