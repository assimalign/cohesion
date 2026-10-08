using System;
using System.Diagnostics.Tracing;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Database.Internal;
using Assimalign.Cohesion.Database.Transactions;

namespace Assimalign.Cohesion.Database;

/// <summary>
/// The base of every explicit ACID transaction a session begins: one copy of the end state machine
/// the models share (#1188, #1225, #1226), over a kernel transaction the leaf owns.
/// </summary>
/// <remarks>
/// <para>
/// <b>The end gate.</b> Commit, rollback, disposal, an abort for a failed operation
/// (<see cref="AbortAsync"/>) and the session's teardown (<see cref="CloseAsync"/>) all end the
/// kernel transaction, and they never race into the kernel: one gate serializes them, so each sees
/// the outcome of the one before it.
/// </para>
/// <para>
/// <b>Cancellation is observed only before an end starts.</b> A token canceled by then leaves the
/// transaction as it was. A commit or rollback that started runs to completion: the leaf's cores
/// take no token, because a rollback stopped half way would leave the transaction holding its
/// locks, and a canceled commit could only turn into an abort of work the caller asked to keep
/// (PostgreSQL holds interrupts through <c>AbortTransaction</c> for the same reason,
/// <c>src/backend/access/transam/xact.c:2860-2861</c>).
/// </para>
/// <para>
/// <b>A transaction that did not commit accepts any number of rollbacks</b>, because its work is
/// already undone once one completed or the kernel aborted it; a committed transaction refuses one.
/// A commit ends the transaction whatever its outcome, as a failed transaction block's
/// <c>COMMIT</c> does in PostgreSQL (<c>EndTransactionBlock</c>, <c>xact.c:4133-4139</c>): one
/// after an operation aborted the transaction completes the rollback and fails with the model's
/// coded error (<see cref="CreateAbortedException"/>), and commits nothing.
/// </para>
/// <para>
/// <b>Faulted.</b> <see cref="State"/> reports <see cref="TransactionState.Faulted"/> while the
/// transaction waits for its caller to end it after something else did: an operation aborted it
/// (<see cref="AbortAsync"/>; Graph, Documents and Blob abort on a failed statement, while SQL and
/// key-value statements are statement-atomic and never call it), or the kernel ended it under its
/// caller. The transaction stays the session's transaction meanwhile
/// (<see cref="IsOpen"/>), and the session refuses work with <see cref="CreateRefusal"/> until the
/// caller rolls it back.
/// </para>
/// <para>
/// <b>An offline database (#1243)</b> refuses a commit and a rollback before they start
/// (<see cref="GetOfflineRefusal"/>), and disposal, the session's teardown and an abort touch
/// nothing on it: the reopen's recovery aborts the transaction, which has no commit record.
/// </para>
/// <para>
/// <b>What a leaf supplies.</b> The kernel state (<see cref="GetKernelState"/>), the kernel commit
/// and rollback with its own exception translation (<see cref="CommitCoreAsync"/>,
/// <see cref="RollbackCoreAsync"/>), its offline refusal and its coded aborted error. The leaves
/// live in the model assemblies, so the constructor is <c>protected</c>; the identity and
/// isolation level are fixed by it (concrete-types plan, phase 3, #1259; §6.4).
/// </para>
/// </remarks>
// Deviates from the repo interface-first rule per design decision: Database engines are concrete-first — abstract bases with protected cores and sealed model leaves (owner, 2026-10-04; database-area.md).
public abstract class DatabaseTransaction : IAsyncDisposable
{
    // The causes TransactionRolledBack writes (event-sources plan, event 31).
    private const string rollbackCause = "Rollback";
    private const string disposeCause = "Dispose";
    private const string sessionClosedCause = "SessionClosed";

    private readonly TransactionId _id;
    private readonly IsolationLevel _isolationLevel;
    private readonly object _sync = new();

    // Serializes every path that ends the kernel transaction (commit, rollback, dispose, abort,
    // close), so two of them never race into the kernel and each sees the outcome of the one before.
    private readonly SemaphoreSlim _endGate = new(1, 1);

    // The failure that aborted the transaction, or why the session's teardown ended it; recorded
    // only while the caller has not ended the transaction itself.
    private Exception? _failure;

    // Whether the caller (or the session's teardown) ended the transaction: a commit or rollback
    // started, or the transaction was disposed or closed.
    private bool _ended;

    // The operations of the transaction still running (statements, commands, streams); a commit
    // is refused while any runs.
    private int _operations;
    private int _disposed;

    /// <summary>
    /// Initializes a new transaction with its identity and isolation level.
    /// </summary>
    /// <param name="id">The unique identifier of the transaction.</param>
    /// <param name="isolationLevel">The isolation level the transaction was begun at.</param>
    protected DatabaseTransaction(TransactionId id, IsolationLevel isolationLevel)
    {
        _id = id;
        _isolationLevel = isolationLevel;
    }

    /// <summary>
    /// Gets the unique identifier of the transaction.
    /// </summary>
    public TransactionId Id => _id;

    /// <summary>
    /// Gets the isolation level the transaction was begun at. An engine may execute at a stronger
    /// level than requested, never weaker.
    /// </summary>
    public IsolationLevel IsolationLevel => _isolationLevel;

    /// <summary>
    /// Gets the state of the transaction: the kernel transaction's state once the caller ended it,
    /// <see cref="TransactionState.Faulted"/> while an operation's failure aborted it or the kernel
    /// ended it under the caller, and <see cref="TransactionState.Active"/> otherwise, including
    /// while the caller's commit or rollback runs.
    /// </summary>
    public TransactionState State
    {
        get
        {
            lock (_sync)
            {
                var state = GetKernelState();
                if (state != TransactionState.Active)
                {
                    // The kernel ended it under the caller: Faulted until the caller ends it.
                    return _ended ? state : TransactionState.Faulted;
                }

                // An active kernel transaction refuses work once an operation failed in it; only a
                // rollback can end it then.
                return _failure is not null ? TransactionState.Faulted : state;
            }
        }
    }

    /// <summary>
    /// Gets whether the transaction is still its session's transaction: the caller has not ended
    /// it, or the caller's commit or rollback is still running.
    /// </summary>
    protected internal bool IsOpen
    {
        get
        {
            lock (_sync)
            {
                return !_ended || GetKernelState() == TransactionState.Active;
            }
        }
    }

    /// <summary>
    /// Gets whether operations may run in the transaction: it is open, not aborted, and its kernel
    /// transaction is active.
    /// </summary>
    protected internal bool IsUsable
    {
        get
        {
            lock (_sync)
            {
                return IsUsableLocked;
            }
        }
    }

    /// <summary>
    /// Gets the number of operations of the transaction still running (<see cref="TryBeginOperation"/>).
    /// </summary>
    protected int RunningOperations
    {
        get
        {
            lock (_sync)
            {
                return _operations;
            }
        }
    }

    private bool IsUsableLocked => !_ended && _failure is null && GetKernelState() == TransactionState.Active;

    /// <summary>
    /// Commits the transaction, or, when an operation aborted it or the kernel ended it under its
    /// caller, completes its rollback and fails with the model's coded error. A commit ends the
    /// transaction whatever its outcome.
    /// </summary>
    /// <param name="cancellationToken">Observed only until the commit starts.</param>
    /// <returns>A task that completes once the commit ended.</returns>
    /// <exception cref="OperationCanceledException">The token was canceled before the commit started.</exception>
    /// <exception cref="DatabaseException">
    /// The transaction was aborted or already ended (nothing was committed), or an operation of the
    /// transaction is still running (the transaction stays active); the model's own errors besides.
    /// </exception>
    public async ValueTask CommitAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        long started = DatabaseEventSource.Log.StartTimer(EventLevel.Verbose, DatabaseEventSource.Keywords.Transactions);

        // The filter writes a failed commit and never catches it: the exception leaves exactly as
        // it would untraced.
        try
        {
            // An offline database refuses the commit before it starts (#1243): nothing is written,
            // and the reopen's recovery aborts the transaction, which has no commit record.
            ThrowIfOffline();
            await _endGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                Exception? failure;
                bool aborted;
                lock (_sync)
                {
                    var state = GetKernelState();
                    if (_ended && state != TransactionState.Active)
                    {
                        // A transaction an operation aborted keeps reporting why nothing committed, so
                        // the outcome never depends on whether the session's teardown ended it first.
                        throw _failure is not null
                            ? CreateAbortedException(_failure, commit: true)
                            : new DatabaseException($"The transaction is {state}.");
                    }

                    failure = _failure;

                    // Under the end gate an ended transaction whose kernel transaction is still active
                    // had a commit or rollback the kernel refused before it started (it refuses every
                    // end while the database closes); nothing the caller rolled back may commit then.
                    aborted = failure is not null || state != TransactionState.Active || _ended;
                    if (!aborted && _operations != 0)
                    {
                        // The operation would race the commit record. The caller completes it first, as
                        // with any statement of an explicit transaction.
                        throw new DatabaseException("An operation of the transaction is still running; commit after it completes.");
                    }

                    _ended = true;
                }

                if (!aborted)
                {
                    await CommitCoreAsync().ConfigureAwait(false);
                    DatabaseEventSource.Log.TransactionCommitted(this, started);
                    return;
                }

                // Nothing may commit. An abort's own rollback normally ran already; this one completes
                // it when the commit took the end gate first.
                await RollbackKernelAsync().ConfigureAwait(false);
                throw CreateAbortedException(failure, commit: true);
            }
            finally
            {
                _endGate.Release();
            }
        }
        catch (Exception exception) when (TraceCommitFailure(exception))
        {
            throw;
        }
    }

    /// <summary>
    /// Rolls the transaction back. A transaction that did not commit accepts any number of
    /// rollbacks; a committed transaction refuses one. A rollback that started runs to completion
    /// and ends the transaction whatever fails.
    /// </summary>
    /// <param name="cancellationToken">Observed only until the rollback starts.</param>
    /// <returns>A task that completes once the rollback ended.</returns>
    /// <exception cref="OperationCanceledException">The token was canceled before the rollback started.</exception>
    /// <exception cref="DatabaseException">
    /// The transaction is committed, or its database is offline (the model's refusal, #1243); the
    /// kernel's own refusal of the rollback, translated by the model, besides.
    /// </exception>
    public async ValueTask RollbackAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        // An offline database refuses the rollback too (#1243): its undo could write nothing, and
        // the reopen's recovery aborts the transaction.
        ThrowIfOffline();
        await _endGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            lock (_sync)
            {
                if (GetKernelState() == TransactionState.Committed)
                {
                    throw new DatabaseException("The transaction is Committed; a committed transaction cannot roll back.");
                }

                _ended = true;
            }

            var rollback = RollbackKernelAsync(out bool rolledBack);
            await rollback.ConfigureAwait(false);
            if (rolledBack)
            {
                DatabaseEventSource.Log.TransactionRolledBack(this, rollbackCause);
            }
        }
        finally
        {
            _endGate.Release();
        }
    }

    /// <summary>
    /// Ends the transaction: rolls it back if its kernel transaction is still active, then releases
    /// what the leaf holds (<see cref="DisposeAsyncCore"/>, once). Rolls back nothing on an offline
    /// database.
    /// </summary>
    /// <returns>A task that completes once the transaction is ended.</returns>
    /// <exception cref="DatabaseException">
    /// The kernel refused the rollback (the model's translation); the leaf's
    /// <see cref="DisposeAsyncCore"/> still ran.
    /// </exception>
    public async ValueTask DisposeAsync()
    {
        try
        {
            await _endGate.WaitAsync().ConfigureAwait(false);
            try
            {
                lock (_sync)
                {
                    _ended = true;
                }

                var rollback = RollbackKernelAsync(out bool rolledBack);
                await rollback.ConfigureAwait(false);
                if (rolledBack)
                {
                    DatabaseEventSource.Log.TransactionRolledBack(this, disposeCause);
                }
            }
            finally
            {
                _endGate.Release();
            }
        }
        finally
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                await DisposeAsyncCore().ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// Aborts the transaction because an operation failed in it: records the first failure, then
    /// rolls the kernel transaction back so it holds no locks while it waits for the caller's
    /// rollback. The failure is recorded first, so the session refuses later operations even when
    /// the rollback itself fails. A transaction its caller already ended keeps its own outcome.
    /// </summary>
    /// <param name="cause">The failure the caller observed from the operation.</param>
    /// <returns>A task that completes once the rollback ended.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="cause"/> is null.</exception>
    protected async ValueTask AbortAsync(Exception cause)
    {
        ArgumentNullException.ThrowIfNull(cause);
        bool aborted;
        lock (_sync)
        {
            // This failure aborts the transaction when it is the first one the open transaction
            // records; a later failure, or one after its caller ended it, changes nothing.
            aborted = !_ended && _failure is null;
            if (!_ended)
            {
                _failure ??= cause;
            }
        }

        if (aborted)
        {
            DatabaseEventSource.Log.TransactionAborted(this, cause);
        }

        await _endGate.WaitAsync().ConfigureAwait(false);
        try
        {
            await RollbackKernelAsync().ConfigureAwait(false);
        }
        finally
        {
            _endGate.Release();
        }
    }

    /// <summary>
    /// Ends the transaction because its session closed: rolls it back like a disposal, and records
    /// the closure as the cause when nothing aborted it first, so a caller that still holds the
    /// transaction gets the model's coded error naming why nothing committed from a later commit. A
    /// transaction its caller already ended keeps its own outcome.
    /// </summary>
    /// <param name="cause">Why the session closed.</param>
    /// <returns>A task that completes once the rollback ended.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="cause"/> is null.</exception>
    protected internal async ValueTask CloseAsync(Exception cause)
    {
        ArgumentNullException.ThrowIfNull(cause);
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

            var rollback = RollbackKernelAsync(out bool rolledBack);
            await rollback.ConfigureAwait(false);
            if (rolledBack)
            {
                DatabaseEventSource.Log.TransactionRolledBack(this, sessionClosedCause);
            }
        }
        finally
        {
            _endGate.Release();
        }
    }

    /// <summary>
    /// Admits one operation (a statement, a command, a stream) into the transaction, so a commit
    /// cannot start while it runs. Every admitted operation is paired with <see cref="EndOperation"/>.
    /// </summary>
    /// <returns>True when the transaction accepts the operation; false when it refuses operations (<see cref="IsUsable"/>).</returns>
    protected bool TryBeginOperation()
    {
        lock (_sync)
        {
            if (!IsUsableLocked)
            {
                return false;
            }

            _operations++;
            return true;
        }
    }

    /// <summary>
    /// Ends one operation <see cref="TryBeginOperation"/> admitted.
    /// </summary>
    /// <exception cref="InvalidOperationException">No admitted operation is running.</exception>
    protected void EndOperation()
    {
        lock (_sync)
        {
            if (_operations == 0)
            {
                throw new InvalidOperationException("No operation of the transaction is running.");
            }

            _operations--;
        }
    }

    /// <summary>
    /// Creates the error for an operation or BEGIN its session refuses because the transaction is
    /// not usable: the model's coded error when an operation aborted it or the kernel ended it under
    /// its caller, or a plain error while the caller's own commit or rollback is running or has
    /// ended it.
    /// </summary>
    /// <returns>The refusal.</returns>
    protected internal DatabaseException CreateRefusal()
    {
        lock (_sync)
        {
            if (_failure is not null)
            {
                return CreateAbortedException(_failure, commit: false);
            }

            if (_ended)
            {
                return GetKernelState() == TransactionState.Active
                    ? new DatabaseException("The session's transaction is being committed or rolled back; start the operation after it ends.")
                    : new DatabaseException("The session's transaction ended before the operation started; nothing was written.");
            }

            return CreateAbortedException(null, commit: false);
        }
    }

    /// <summary>
    /// Gets the state of the kernel transaction the leaf wraps. Called under the base's lock: it
    /// must not block or call back into the transaction.
    /// </summary>
    /// <returns>The kernel transaction's state.</returns>
    protected abstract TransactionState GetKernelState();

    /// <summary>
    /// Commits the kernel transaction. Called once, under the end gate, while the kernel
    /// transaction is active and no operation runs. It takes no token: a commit that started runs
    /// to completion. A kernel failure crosses the boundary translated into the area root's
    /// exceptions (<see cref="DatabaseTransactionAbortedException"/>,
    /// <see cref="DatabaseTransactionCommitUnconfirmedException"/>, the model's offline refusal).
    /// </summary>
    /// <returns>A task that completes once the kernel transaction committed.</returns>
    protected abstract ValueTask CommitCoreAsync();

    /// <summary>
    /// Rolls the kernel transaction back. Called under the end gate, only while the kernel
    /// transaction is active and the database is online. It takes no token: a rollback that started
    /// runs to completion, and the kernel ends the transaction whatever fails (#1226). A kernel
    /// refusal crosses the boundary translated.
    /// </summary>
    /// <returns>A task that completes once the kernel transaction ended.</returns>
    protected abstract ValueTask RollbackCoreAsync();

    /// <summary>
    /// Gets the model's refusal while the database cannot serve the transaction because it is
    /// offline (#1243), or null while it is online. Called before a commit or rollback starts, and
    /// before every kernel rollback, which an offline database skips.
    /// </summary>
    /// <returns>The refusal to throw, or null.</returns>
    protected abstract DatabaseException? GetOfflineRefusal();

    /// <summary>
    /// Creates the model's coded error for work refused by an aborted transaction (for example
    /// <c>COHDBG007</c> in the graph model). The cause travels in the message as well as the inner
    /// exception, because the wire carries message text only. Called under the base's lock: it
    /// must only build the exception.
    /// </summary>
    /// <param name="cause">The failure that aborted the transaction, or null when the kernel ended it.</param>
    /// <param name="commit">True when the refused work is the transaction's commit.</param>
    /// <returns>The coded error, carrying <paramref name="cause"/> as its inner exception.</returns>
    protected abstract DatabaseException CreateAbortedException(Exception? cause, bool commit);

    /// <summary>
    /// Releases what the leaf holds once the transaction was ended by its first disposal. The
    /// default does nothing.
    /// </summary>
    /// <returns>A task that completes once the leaf's resources are released.</returns>
    protected virtual ValueTask DisposeAsyncCore() => ValueTask.CompletedTask;

    private void ThrowIfOffline()
    {
        if (GetOfflineRefusal() is { } refusal)
        {
            throw refusal;
        }
    }

    // Runs under the end gate. Only an active kernel transaction on an online database is rolled
    // back; the kernel ends it whatever fails once the rollback starts.
    private ValueTask RollbackKernelAsync() => RollbackKernelAsync(out _);

    // The same, telling its caller whether the kernel rollback started, for the rolled-back event.
    private ValueTask RollbackKernelAsync(out bool started)
    {
        if (GetKernelState() != TransactionState.Active || GetOfflineRefusal() is not null)
        {
            started = false;
            return ValueTask.CompletedTask;
        }

        started = true;
        return RollbackCoreAsync();
    }

    // An exception filter: writes a failed commit and returns false, so nothing is caught. A token
    // canceled before the commit started is not a failed commit.
    private bool TraceCommitFailure(Exception exception)
    {
        if (exception is not OperationCanceledException)
        {
            DatabaseEventSource.Log.TransactionCommitFailed(this, exception);
        }

        return false;
    }
}
