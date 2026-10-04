using System;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Database.KeyValuePair.Internal;

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
/// <remarks>
/// A command is statement-atomic, so a failed command leaves the transaction active (DESIGN.md,
/// "Failed commands in explicit transactions"). The transaction's own end follows the #1188
/// contract: a transaction that did not commit accepts any number of rollbacks, a token is
/// observed only before a commit or rollback starts, and a commit or rollback that does not
/// complete leaves the transaction <see cref="TransactionState.Faulted"/>, refusing commands and
/// BEGIN with <c>COHDBK001</c> until a rollback completes.
/// </remarks>
internal sealed class KeyValueDatabaseTransaction : IDatabaseTransaction
{
    private readonly TransactionCoordinator _coordinator;
    private readonly ITransactionContext _context;
    private readonly object _sync = new();

    // Serializes every path that ends the context (commit, rollback, dispose), so two of them never
    // race into the coordinator and each one sees the outcome of the one before it: a host's
    // rollback can meet the server session's teardown disposing the same transaction.
    private readonly SemaphoreSlim _endGate = new(1, 1);
    private Exception? _endFailure;
    private bool _ended;

    internal KeyValueDatabaseTransaction(TransactionCoordinator coordinator, ITransactionContext context)
    {
        _coordinator = coordinator;
        _context = context;
    }

    /// <inheritdoc />
    public TransactionId Id => _context.Id;

    /// <inheritdoc />
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
                // An active context whose commit or rollback did not complete refuses commands;
                // only a rollback can end it. A commit or rollback still in flight reports Active.
                return _endFailure is not null ? TransactionState.Faulted : state;
            }
        }
    }

    /// <inheritdoc />
    public IsolationLevel IsolationLevel => _context.IsolationLevel;

    /// <summary>
    /// Gets the MVCC transaction context commands execute under: the executor
    /// stamps writes with its sequence and resolves reads through its snapshot
    /// (re-captured per command under <see cref="IsolationLevel.ReadCommitted"/>,
    /// fixed at begin under <see cref="IsolationLevel.Snapshot"/>).
    /// </summary>
    internal ITransactionContext Context => _context;

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

    /// <summary>Gets whether commands may run in the transaction: it is open, active and its end did not fail.</summary>
    internal bool IsUsable
    {
        get
        {
            lock (_sync)
            {
                return !_ended && _endFailure is null && _context.State == TransactionState.Active;
            }
        }
    }

    /// <inheritdoc />
    /// <remarks>
    /// The token is observed only until the commit starts; a token canceled by then leaves the
    /// transaction as it was. A commit that started runs to completion: a cancellation there could
    /// only turn into a kernel abort of work the caller asked to keep. When a rollback did not
    /// complete, the commit completes it and fails with <c>COHDBK001</c>, committing nothing.
    /// </remarks>
    public async ValueTask CommitAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await _endGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Exception? failure;
            lock (_sync)
            {
                var state = _context.State;
                if (_ended && state != TransactionState.Active)
                {
                    throw new DatabaseException($"Cannot commit transaction in state '{state}'.");
                }
                if (!_ended && state != TransactionState.Active)
                {
                    // The kernel ended the context under the caller; nothing of it can commit.
                    _ended = true;
                    throw CreateAbortedException(null, commit: true);
                }
                // Under the end gate an ended transaction whose context is still active is one whose
                // commit or rollback did not complete, so it carries an end failure.
                failure = _endFailure;
                _ended = true;
            }
            if (failure is null)
            {
                try
                {
                    await _coordinator.CommitAsync(_context).ConfigureAwait(false);
                }
                catch (Exception error)
                {
                    // A kernel abort of the commit crosses the engine boundary as the area root's exception.
                    RecordEndFailure(error);
                    var translated = Translate(error);
                    if (ReferenceEquals(translated, error)) { throw; }
                    throw translated;
                }
                return;
            }
            await RollbackContextAsync().ConfigureAwait(false);
            throw CreateAbortedException(failure, commit: true);
        }
        finally
        {
            _endGate.Release();
        }
    }

    /// <inheritdoc />
    /// <remarks>
    /// A transaction that did not commit accepts any number of rollbacks, because its work is
    /// already undone once one has completed or the kernel aborted it; a committed transaction
    /// refuses one, because nothing can undo its work. The token is observed only until the
    /// rollback starts: a rollback stopped half way would leave the transaction active, holding its
    /// key locks, with its undo queued for the version-purge worker.
    /// </remarks>
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
                    throw new DatabaseException("Cannot rollback transaction in state 'Committed': a committed transaction cannot roll back.");
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

    /// <inheritdoc />
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
    /// Creates the error for a command or BEGIN the session refuses because the transaction is not
    /// usable: <c>COHDBK001</c> when its end did not complete or the kernel ended it, or a plain
    /// error while the caller's own commit or rollback is still in flight.
    /// </summary>
    /// <returns>The refusal.</returns>
    internal DatabaseException CreateRefusal()
    {
        lock (_sync)
        {
            if (_endFailure is not null)
            {
                return new DatabaseException(
                    "COHDBK001: The session's transaction is aborted: its commit or rollback did not complete, and commands are " +
                    "refused until RollbackAsync completes. Cause: " + _endFailure.Message, _endFailure);
            }
            if (_ended && _context.State == TransactionState.Active)
            {
                return new DatabaseException("The session's transaction is being committed or rolled back; start the command after it ends.");
            }
            return CreateAbortedException(null);
        }
    }

    /// <summary>Creates the <c>COHDBK001</c> error for work refused by an aborted transaction.</summary>
    /// <param name="failure">The failure that aborted the transaction, or null when the kernel ended it.</param>
    /// <param name="commit">True when the refused work is the transaction's commit.</param>
    /// <returns>The coded error, carrying <paramref name="failure"/> as its inner exception.</returns>
    private static DatabaseException CreateAbortedException(Exception? failure, bool commit = false)
    {
        // The cause travels in the message as well: the wire carries message text, not inner exceptions.
        string cause = failure is null ? string.Empty : " Cause: " + failure.Message;
        string message = commit
            ? "COHDBK001: The session's transaction is aborted and cannot commit; nothing was committed." + cause
            : "COHDBK001: The session's transaction is aborted; commands are refused until it is rolled back." + cause;
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
            var translated = Translate(error);
            if (ReferenceEquals(translated, error)) { throw; }
            throw translated;
        }
    }

    // A commit or rollback that failed while the context stayed active leaves a transaction that
    // only a rollback can end; State reports it Faulted and the session refuses commands in it.
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

    // The area error policy: the engine translates the transaction kernel's independent exception
    // root at the model boundary.
    private static Exception Translate(Exception error) => error switch
    {
        TransactionDeadlockException => new DatabaseTransactionDeadlockException(error.Message, error),
        TransactionAbortedException => new DatabaseTransactionAbortedException(error.Message, error),
        _ => error,
    };
}
