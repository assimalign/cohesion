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
/// observed only before a commit or rollback starts, and a transaction the kernel ended under its
/// caller reports <see cref="TransactionState.Faulted"/> and refuses commands, BEGIN and COMMIT
/// with <c>COHDBK001</c> until the caller ends it. A commit or rollback that started always ends
/// the context (#1226): the kernel completes a started rollback whatever fails, keeping the key
/// locks of a writer whose undo it must defer, and aborts a commit it cannot complete. A commit
/// waits for no command: one that starts while a command of the transaction runs is refused. A
/// rollback ends the transaction even under a running command, which then fails and writes
/// nothing (the kernel refuses its bracket, fails a key-lock request it still has queued, and a
/// key lock granted to the ended transaction is given back).
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

    // Why the session's teardown ended the transaction under its caller, so the caller's
    // commit afterwards reports COHDBK001 with the cause, not a bare state.
    private Exception? _closedBy;
    private int _commands;
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
                // A failed command leaves the transaction active, and a commit or rollback still
                // in flight reports Active.
                return state;
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

    /// <summary>Gets whether commands may run in the transaction: the caller has not ended it and its context is active.</summary>
    internal bool IsUsable
    {
        get
        {
            lock (_sync)
            {
                return IsUsableLocked;
            }
        }
    }

    private bool IsUsableLocked => !_ended && _context.State == TransactionState.Active;

    /// <summary>
    /// Admits one command into the transaction, so a commit cannot start while it runs. Every
    /// admitted command is paired with <see cref="EndCommand"/>.
    /// </summary>
    /// <returns>True when the transaction accepts the command; false when it refuses commands.</returns>
    internal bool TryBeginCommand()
    {
        lock (_sync)
        {
            if (!IsUsableLocked)
            {
                return false;
            }

            _commands++;
            return true;
        }
    }

    /// <summary>Gets the number of admitted commands still running (observability for tests and diagnostics).</summary>
    internal int RunningCommands
    {
        get
        {
            lock (_sync)
            {
                return _commands;
            }
        }
    }

    /// <summary>Ends one command admitted by <see cref="TryBeginCommand"/>.</summary>
    internal void EndCommand()
    {
        lock (_sync)
        {
            _commands--;
        }
    }

    /// <inheritdoc />
    /// <remarks>
    /// The token is observed only until the commit starts; a token canceled by then leaves the
    /// transaction as it was. A commit that started runs to completion: a cancellation there could
    /// only turn into a kernel abort of work the caller asked to keep. A commit while a command of
    /// the transaction still runs is refused before it starts and leaves the transaction active. A
    /// commit after the session's teardown ended the transaction fails with <c>COHDBK001</c>
    /// naming the teardown, and a commit after the kernel ended it, or after an earlier commit or
    /// rollback the kernel refused before it started (the database closing), fails with
    /// <c>COHDBK001</c> and commits nothing.
    /// </remarks>
    public async ValueTask CommitAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await _endGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            bool aborted;
            lock (_sync)
            {
                var state = _context.State;
                if (_ended && state != TransactionState.Active)
                {
                    throw _closedBy is not null
                        ? CreateAbortedException(_closedBy, commit: true)
                        : new DatabaseException($"Cannot commit transaction in state '{state}'.");
                }
                if (!_ended && state != TransactionState.Active)
                {
                    // The kernel ended the context under the caller; nothing of it can commit.
                    _ended = true;
                    throw CreateAbortedException(null, commit: true);
                }

                // Under the end gate an ended transaction whose context is still active had a
                // commit or rollback that threw before the kernel started it. The kernel ends every
                // started rollback (#1226), but refuses one before it starts while the database
                // closes (disposal claimed the end, or the manager is disposed); nothing the caller
                // rolled back may commit then.
                aborted = _ended;
                if (!aborted && _commands != 0)
                {
                    // The command's bracket would race the commit record. The caller awaits its
                    // command first, as with any statement of an explicit transaction.
                    throw new DatabaseException("A command of the transaction is still running; commit after it completes.");
                }
                _ended = true;
            }
            if (!aborted)
            {
                try
                {
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
            await RollbackContextAsync().ConfigureAwait(false);
            throw CreateAbortedException(null, commit: true);
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
    /// Ends the transaction because its session closed: rolls it back like a disposal, and
    /// records why, so a caller that still holds the transaction (a host that opened it on a wire
    /// session) gets <c>COHDBK001</c> naming the closure from a later commit, whichever of its
    /// commit and the teardown runs first. A transaction its caller already ended keeps its own
    /// outcome.
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
                    _closedBy = cause;
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
    /// Creates the error for a command or BEGIN the session refuses because the transaction is not
    /// usable: <c>COHDBK001</c> when the kernel ended it under its caller, or a plain error while
    /// the caller's own commit or rollback is still in flight or has ended it.
    /// </summary>
    /// <returns>The refusal.</returns>
    internal DatabaseException CreateRefusal()
    {
        lock (_sync)
        {
            if (_ended)
            {
                return _context.State == TransactionState.Active
                    ? new DatabaseException("The session's transaction is being committed or rolled back; start the command after it ends.")
                    : new DatabaseException("The session's transaction ended before the command started; nothing was written.");
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
    // not passed on (PostgreSQL holds interrupts through AbortTransaction for the same reason), and
    // the kernel ends the context whatever fails once it starts (#1226). Only a closing database
    // refuses the rollback before it starts, and its disposal then aborts the context itself.
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
            var translated = Translate(error);
            if (ReferenceEquals(translated, error)) { throw; }
            throw translated;
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
