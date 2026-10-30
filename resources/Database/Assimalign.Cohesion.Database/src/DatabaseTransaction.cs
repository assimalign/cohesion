using System;
using System.Collections.Generic;
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
/// A commit that starts ends the transaction whatever its outcome, as a failed transaction block's
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
/// <b>Incomplete partial undo.</b> The kernel stays active while <see cref="State"/> reports
/// <see cref="TransactionState.Faulted"/> and statements and commit refuse with CK04009.
/// Only the owning session's rollback to the same or an outer savepoint, or whole rollback,
/// recovers it. This differs from a model statement failure that aborted the whole transaction.
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
/// initial isolation level are supplied by it (concrete-types plan, phase 3, #1259; §6.4).
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
    private IsolationLevel _isolationLevel;
    private readonly object _sync = new();

    // Serializes every path that ends the kernel transaction (commit, rollback, dispose, abort,
    // close), so two of them never race into the kernel and each sees the outcome of the one before.
    private readonly SemaphoreSlim _endGate = new(1, 1);

    // The failure that aborted the transaction, or why the session's teardown ended it; recorded
    // only while the caller has not ended the transaction itself.
    // An aborted transaction cannot use its names again. The normal path and the lazy
    // name stack therefore share the former failure reference, keeping unmarked object size.
    private object? _scopeState;
    private Exception? Failure
    {
        get => _scopeState as Exception;
        set => _scopeState = value;
    }
    private List<(string Name, TransactionSavepoint Token)>? Savepoints
    {
        get => _scopeState as List<(string Name, TransactionSavepoint Token)>;
        set
        {
            // End cleanup must retain an abort cause for later CommitAsync refusals.
            if (value is not null || _scopeState is List<(string Name, TransactionSavepoint Token)>) { _scopeState = value; }
        }
    }

    // Whether the caller (or the session's teardown) ended the transaction: a commit or rollback
    // started, or the transaction was disposed or closed.
    private bool _ended;

    // The operations of the transaction still running (statements, commands, streams); a commit
    // is refused while any runs.
    private int _operations;
    private int _disposed;
    private readonly TransactionCoordinator? _savepointCoordinator;
    private TransactionContext? _savepointContext;

    /// <summary>Initializes a transaction with the kernel binding used for named undo scopes.</summary>
    /// <param name="coordinator">The owning coordinator.</param>
    /// <param name="context">The current transaction context.</param>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    protected DatabaseTransaction(TransactionCoordinator coordinator, TransactionContext context)
        : this((context ?? throw new ArgumentNullException(nameof(context))).Id, context.IsolationLevel)
    {
        ArgumentNullException.ThrowIfNull(coordinator);
        ArgumentNullException.ThrowIfNull(context);
        _savepointCoordinator = coordinator;
        _savepointContext = context;
    }

    /// <summary>Gets the coordinator supplied by the kernel-bound constructor.</summary>
    /// <exception cref="InvalidOperationException">The transaction has no kernel binding.</exception>
    protected TransactionCoordinator KernelCoordinator => _savepointCoordinator ?? throw new InvalidOperationException(ErrorMessages.SavepointBindingMissing);

    /// <summary>Gets the current context supplied by the kernel-bound constructor or isolation replacement.</summary>
    /// <exception cref="InvalidOperationException">The transaction has no kernel binding.</exception>
    protected TransactionContext KernelContext => _savepointContext ?? throw new InvalidOperationException(ErrorMessages.SavepointBindingMissing);

    /// <summary>Updates the current context after an admitted isolation replacement.</summary>
    /// <param name="context">The replacement lifecycle handle.</param>
    /// <exception cref="ArgumentNullException">The context is null.</exception>
    protected void UpdateSavepointContext(TransactionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        _savepointContext = context;
    }

    /// <summary>Gets whether the transaction currently contains a named undo scope.</summary>
    protected internal bool HasSavepoints => Savepoints is { Count: > 0 };
    internal bool PartialUndoPending => UndoPending;
    private bool UndoPending => _savepointContext is not null && TransactionCoordinator.IsPartialUndoPending(_savepointContext);

    // Session operation -> transaction end gate -> root monitor. Release the monitor before
    // manager admission -> context end latch -> ledger monitor/lock table; no root monitor
    // spans storage/page/index latches or awaits. _operations == -1 excludes direct typed
    // operations while a control owns the gate. Finally restores it on every failure path.
    internal async ValueTask ExecuteSavepointAsync(string name, DatabaseSavepointOperation operation, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        cancellationToken.ThrowIfCancellationRequested();
        ThrowIfOffline();
        await _endGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        bool admitted = false;
        int targetIndex = -1;
        try
        {
            lock (_sync)
            {
                if (_ended || Failure is not null || GetKernelState() != TransactionState.Active) { throw CreateRefusal(); }
                if (_operations != 0) { throw ControlBusyRefusal(); }
                if (operation != DatabaseSavepointOperation.Rollback && UndoPending) { throw UndoRefusal(); }
                _operations = -1;
                admitted = true;
            }
            if (_savepointCoordinator is null || _savepointContext is null)
            { throw DatabaseException.CreateRootFailure("CK04012", ErrorMessages.SavepointBindingMissing); }
            if (operation == DatabaseSavepointOperation.Create)
            {
                var scopes = Savepoints ??= new();
                scopes.EnsureCapacity(scopes.Count + 1);
                var token = await _savepointCoordinator.CreateSavepointAsync(_savepointContext, cancellationToken).ConfigureAwait(false);
                scopes.Add((name, token));
                return;
            }
            int index = Savepoints?.FindLastIndex(scope => string.Equals(scope.Name, name, StringComparison.Ordinal)) ?? -1;
            if (index < 0)
            {
                if (UndoPending) { throw UndoRefusal(); }
                throw DatabaseException.CreateRootFailure("CK04013", ErrorMessages.SavepointUnknown);
            }
            targetIndex = index;
            if (operation == DatabaseSavepointOperation.Release)
            {
                await _savepointCoordinator.ReleaseSavepointAsync(_savepointContext, Savepoints![index].Token, cancellationToken).ConfigureAwait(false);
                Savepoints.RemoveRange(index, Savepoints.Count - index);
            }
            else
            {
                await _savepointCoordinator.RollbackToSavepointAsync(_savepointContext, Savepoints![index].Token, cancellationToken).ConfigureAwait(false);
                Savepoints.RemoveRange(index + 1, Savepoints.Count - index - 1);
            }
        }
        catch (Exception failure)
        {
            // Admitted rollback discards descendants before its first physical batch. Mirror
            // that scope lifetime even if undo fails; the retained target remains retryable.
            if (operation == DatabaseSavepointOperation.Rollback && targetIndex >= 0 && UndoPending)
            { Savepoints!.RemoveRange(targetIndex + 1, Savepoints.Count - targetIndex - 1); }
            if (TransactionCoordinator.GetUndoFailureCode(failure) is { } code)
            {
                throw new DatabaseException(failure.Message, [new Assimalign.Cohesion.Database.Types.Diagnostic
                { Code = code, Message = failure.Message, Severity = Assimalign.Cohesion.Database.Types.DiagnosticSeverity.Error }], failure);
            }
            throw;
        }
        finally
        {
            if (admitted) { lock (_sync) { _operations = 0; } }
            if (_ended) { Savepoints = null; }
            _endGate.Release();
        }
    }

    private static DatabaseException ControlBusyRefusal() => DatabaseException.CreateRootFailure("CK04009", ErrorMessages.SavepointControlBusy);

    private static DatabaseException UndoRefusal() => DatabaseException.CreateRootFailure("CK04009", ErrorMessages.SavepointUndoPending);

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
    /// Gets the effective isolation level, including an admitted early configuration. An engine may execute at a stronger
    /// level than requested, never weaker.
    /// </summary>
    public IsolationLevel IsolationLevel => _isolationLevel;

    /// <summary>Changes the isolation of an unused transaction while excluding operation admission and transaction ends.</summary>
    /// <param name="isolationLevel">The effective isolation level after configuration.</param>
    /// <param name="configure">Replaces the model's context synchronously without waiting on engine locks; false refuses the change.</param>
    /// <returns>True if configuration completed; false if the model refuses, the transaction is ending or operations are running.</returns>
    /// <exception cref="ArgumentNullException">The configuration action is null.</exception>
    // Deviates from the repo database-area.md rule 6 (state fixed at construction) per design decision: SQL SET TRANSACTION may change the isolation level before the first data statement (#1012); pending owner sign-off.
    protected bool TryConfigureIsolationLevel(IsolationLevel isolationLevel, Func<bool> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        lock (_sync)
        {
            // Lock order: root transaction state -> manager registration -> context end latch.
            // The coordinator's tracking lock follows manager release. No storage/page latch or
            // lock-manager grant is acquired here. End cores run after releasing root state.
            if (!IsUsableLocked || _operations != 0 || HasSavepoints) { return false; }
            if (!configure()) { return false; }
            _isolationLevel = isolationLevel;
            return true;
        }
    }

    /// <summary>
    /// Gets the state of the transaction: the kernel transaction's state once the caller ended it,
    /// <see cref="TransactionState.Faulted"/> while an operation's failure aborted it or the kernel
    /// ended it under the caller or partial undo needs recovery, and <see cref="TransactionState.Active"/> otherwise, including
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
                return Failure is not null || UndoPending ? TransactionState.Faulted : state;
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
    /// transaction is active and no control or incomplete undo fences it.
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

    private bool IsUsableLocked => !_ended && Failure is null && _operations >= 0 && GetKernelState() == TransactionState.Active && !UndoPending;

    /// <summary>
    /// Commits the transaction, or, when an operation aborted it or the kernel ended it under its
    /// caller, completes its rollback and fails with the model's coded error. A commit ends the
    /// transaction once the commit starts, whatever its outcome.
    /// </summary>
    /// <param name="cancellationToken">Observed only until the commit starts.</param>
    /// <returns>A task that completes once the commit ended.</returns>
    /// <exception cref="OperationCanceledException">The token was canceled before the commit started.</exception>
    /// <exception cref="DatabaseException">
    /// The transaction was aborted or already ended (nothing was committed), or an operation of the
    /// transaction is still running or partial undo needs recovery (the transaction stays open); the model's own errors besides.
    /// </exception>
    public async ValueTask CommitAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        long started = DatabaseEventSource.Log.StartTimer(EventLevel.Verbose, DatabaseEventSource.Keywords.Transactions);

        // The filter captures a failed commit and catches nothing; the outer finally writes it once
        // the inner blocks released the lock and the end gate.
        Exception? thrown = null;
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
                        throw Failure is not null
                            ? CreateAbortedException(Failure, commit: true)
                            : DatabaseException.CreateRootFailure("CK04005", string.Format(System.Globalization.CultureInfo.CurrentCulture, ErrorMessages.TransactionCommitAsyncDatabase, state));
                    }

                    failure = Failure;

                    // Under the end gate an ended transaction whose kernel transaction is still active
                    // had a commit or rollback the kernel refused before it started (it refuses every
                    // end while the database closes); nothing the caller rolled back may commit then.
                    aborted = failure is not null || state != TransactionState.Active || _ended;
                    if (!aborted && _operations != 0)
                    {
                        // The operation would race the commit record. The caller completes it first, as
                        // with any statement of an explicit transaction.
                        throw DatabaseException.CreateRootFailure("CK04007", ErrorMessages.TransactionCommitAsyncDatabase2);
                    }

                    if (!aborted && UndoPending) { throw UndoRefusal(); }
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
                if (_ended) { Savepoints = null; }
                _endGate.Release();
            }
        }
        catch (Exception exception) when (DatabaseEventSource.CaptureFailure(exception, out thrown))
        {
            throw;
        }
        finally
        {
            if (thrown is not null)
            {
                TraceCommitFailure(this, thrown);
            }
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
                    throw DatabaseException.CreateRootFailure("CK04005", ErrorMessages.TransactionRollbackAsyncDatabase);
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
            if (_ended) { Savepoints = null; }
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
                if (_ended) { Savepoints = null; }
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
            aborted = !_ended && Failure is null;
            if (!_ended)
            {
                Failure ??= cause;
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
            if (_ended) { Savepoints = null; }
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
                    Failure ??= cause;
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
            if (_ended) { Savepoints = null; }
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
            if (_operations <= 0)
            {
                throw new InvalidOperationException(ErrorMessages.TransactionEndOperationFailure);
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
            if (UndoPending) { return UndoRefusal(); }
            if (_operations < 0) { return ControlBusyRefusal(); }
            if (Failure is not null)
            {
                return CreateAbortedException(Failure, commit: false);
            }

            if (_ended)
            {
                return GetKernelState() == TransactionState.Active
                    ? DatabaseException.CreateRootFailure("CK04006", ErrorMessages.TransactionCreateRefusalDatabase)
                    : DatabaseException.CreateRootFailure("CK04005", ErrorMessages.TransactionCreateRefusalDatabase2);
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
    /// <c>CG04007</c> in the graph model). The cause travels in the message as well as the inner
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

    // Writes a failed commit, after the commit released its lock and its end gate. A token canceled
    // before the commit started is not a failed commit. A commit is an operation, so its failure is
    // written by its exception's type only (the area's failure rule): a commit an operation's
    // failure aborted is refused with the model's error, which repeats that failure's message.
    private static void TraceCommitFailure(DatabaseTransaction transaction, Exception exception)
    {
        if (exception is not OperationCanceledException)
        {
            DatabaseEventSource.Log.TransactionCommitFailed(transaction, exception);
        }
    }
}
