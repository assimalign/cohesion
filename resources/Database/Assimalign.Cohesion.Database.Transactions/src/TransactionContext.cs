using System.Threading.Tasks;

using Assimalign.Cohesion.Database.Transactions.Internal;

namespace Assimalign.Cohesion.Database.Transactions;

/// <summary>
/// The engine-internal state of one in-flight transaction: its identity, sequence,
/// isolation level, and visibility snapshot.
/// </summary>
/// <remarks>
/// <para>
/// Execution operators carry this context to storage, index, and catalog operations
/// so every read resolves through the same snapshot and every write is stamped with
/// the same sequence. It is the engine-side counterpart of the area root's public
/// <c>DatabaseTransaction</c> surface — named, not referenced: this package is a
/// child root the area root aggregates, so it speaks only its own vocabulary.
/// </para>
/// <para>
/// Under <see cref="IsolationLevel.ReadCommitted"/> the snapshot is re-captured on every
/// access (each statement reads it once); under snapshot and serializable isolation it is
/// fixed at begin. A statement that must make one visibility decision across several reads
/// pins it with <see cref="PinStatementSnapshot"/>.
/// </para>
/// <para>
/// The type is sealed, and only a <see cref="TransactionManager"/> creates one
/// (<see cref="TransactionManager.BeginAsync"/>): it is the most-consumed kernel contract, and
/// every call on it is direct.
/// </para>
/// <para>
/// The context carries one end flag with two jobs. It is the claim exactly one
/// commit, rollback or abort wins (#1226): the claim is the point after which a
/// rollback runs to completion whatever fails or is canceled, and it keeps a commit
/// from racing a rollback of the same writer. It is also the admission gate of the
/// context's statement applies (#1225): once the end is claimed, no further apply is
/// admitted, and the end waits for the applies already running. A rollback therefore
/// undoes a ledger no later bracket can add to, and no bracket stamped with the
/// sequence lands after the transaction ended (Neo4j stops a terminated
/// transaction's lock client the same way: it marks the client stopped, waits for the
/// operations in flight, then releases its locks,
/// <c>community/lock/.../forseti/ForsetiClient.java:578-593</c>).
/// </para>
/// <para>
/// One flag, not two, because the two jobs must agree: an end that won the claim but
/// still admitted applies would undo a ledger a later bracket extends, and an apply
/// refused by an end that lost the claim would fail a statement of a transaction
/// that is not ending.
/// </para>
/// </remarks>
[global::System.Diagnostics.DebuggerDisplay("TransactionContext")]
[global::System.Diagnostics.DebuggerTypeProxy(typeof(global::Assimalign.Cohesion.Database.Transactions.TransactionContext.DebugView))]
public sealed class TransactionContext
{
    private const byte snapshotPinFlag = 1;
    private const byte statementViewFlag = 2;
    private const byte endingFlag = 1;
    private const byte partialUndoFlag = 2;

    private readonly TransactionManager _manager;
    private readonly TransactionSnapshot _beginSnapshot;

    // The transaction's original lifecycle owner when this is a statement view or an isolation
    // replacement, null for the context the manager begins. The end flag, apply count and drain
    // signal below live on the transaction's own context only: a view forwards every member
    // that touches them, so one lock always guards one set of state.
    private readonly TransactionContext? _transaction;
    // Share the existing pin-kind byte: another boolean grows this hot context by eight
    // bytes after alignment, including every auto-commit context and statement pin.
    private readonly byte _kind;
    private readonly object _endSync;
    private TransactionState _state;
    private int _applying;
    // Reuse the former end boolean's byte. A second boolean grows every statement pin
    // by eight aligned bytes; lifecycle and pending-undo bits share this existing storage.
    private byte _admission;
    private TaskCompletionSource? _drained;

    internal TransactionContext(
        TransactionManager manager,
        TransactionId id,
        TransactionSequence sequence,
        IsolationLevel isolationLevel,
        TransactionSnapshot beginSnapshot,
        bool isSnapshotPin)
    {
        _manager = manager;
        _endSync = new object();
        Id = id;
        Sequence = sequence;
        IsolationLevel = isolationLevel;
        _beginSnapshot = beginSnapshot;
        _kind = isSnapshotPin ? snapshotPinFlag : (byte)0;
        _state = TransactionState.Active;
    }

    // An immutable statement view or isolation replacement sharing the original lifecycle.
    private TransactionContext(TransactionContext transaction, TransactionSnapshot snapshot,
        IsolationLevel isolationLevel, bool isStatementView)
    {
        _manager = transaction._manager;
        _transaction = transaction._transaction ?? transaction;
        _kind = (byte)((transaction.IsSnapshotPin ? snapshotPinFlag : 0) | (isStatementView ? statementViewFlag : 0));
        _endSync = transaction._endSync;
        Id = transaction.Id;
        Sequence = transaction.Sequence;
        IsolationLevel = isolationLevel;
        _beginSnapshot = snapshot;
    }

    /// <summary>
    /// Gets the manager that created this context, so a manager can reject a
    /// context begun on a different manager instance.
    /// </summary>
    internal TransactionManager Manager => _manager;

    /// <summary>
    /// Gets whether this context is a statement view (<see cref="PinStatementSnapshot"/>) rather
    /// than the context the manager began. A manager ends only its own contexts, never a view.
    /// </summary>
    internal bool IsStatementView => (_kind & statementViewFlag) != 0;

    /// <summary>Gets whether this context only holds a statement snapshot horizon.</summary>
    internal bool IsSnapshotPin => (_kind & snapshotPinFlag) != 0;

    internal TransactionSnapshot BeginSnapshot => _beginSnapshot;

    internal bool PartialUndoPending
    {
        get
        {
            if (_transaction is not null) { return _transaction.PartialUndoPending; }
            lock (_endSync) { return (_admission & partialUndoFlag) != 0; }
        }
    }

    // Called under the manager lock and the coordinator apply gate. The apply count makes
    // rollback/disposal drain this operation. Commit checks the pending flag under the same
    // manager lock before claiming the end, so it cannot publish half an undone suffix.
    internal bool TryEnterPartialUndo()
    {
        if (_transaction is not null) { return _transaction.TryEnterPartialUndo(); }
        lock (_endSync)
        {
            if ((_admission & endingFlag) != 0 || _applying != 0 || State != TransactionState.Active) { return false; }
            _admission |= partialUndoFlag;
            _applying++;
            return true;
        }
    }

    internal void CompletePartialUndo()
    {
        if (_transaction is not null) { _transaction.CompletePartialUndo(); return; }
        lock (_endSync) { _admission = (byte)(_admission & ~partialUndoFlag); }
    }

    /// <summary>
    /// Gets the external identity of the transaction.
    /// </summary>
    public TransactionId Id { get; }

    /// <summary>
    /// Gets the MVCC ordering sequence assigned to the transaction.
    /// </summary>
    public TransactionSequence Sequence { get; }

    /// <summary>
    /// Gets the isolation level the transaction runs under.
    /// </summary>
    public IsolationLevel IsolationLevel { get; }

    /// <summary>
    /// Gets the current state of the transaction. A statement view reports the state of the
    /// transaction it was pinned from.
    /// </summary>
    public TransactionState State
    {
        get => _transaction is null ? _state : _transaction.State;
        internal set
        {
            if (_transaction is null) { _state = value; }
            else { _transaction.State = value; }
        }
    }

    /// <summary>
    /// Gets the visibility snapshot reads resolve through. Under
    /// <see cref="IsolationLevel.ReadCommitted"/> the snapshot is refreshed per
    /// statement; under snapshot and serializable isolation it is fixed at begin. A statement
    /// view's snapshot is the one fixed when it was pinned.
    /// </summary>
    public TransactionSnapshot Snapshot =>
        !IsStatementView && IsolationLevel == IsolationLevel.ReadCommitted && State == TransactionState.Active
            ? _manager.CaptureSnapshot(Sequence)
            : _beginSnapshot;

    /// <summary>
    /// Pins the transaction's current snapshot for one statement: returns a view that shares
    /// <see cref="Id"/>, <see cref="Sequence"/>, <see cref="IsolationLevel"/> and
    /// <see cref="State"/> with the transaction and whose <see cref="Snapshot"/> is fixed at the
    /// moment of this call.
    /// </summary>
    /// <returns>The statement view.</returns>
    /// <remarks>
    /// <para>
    /// One read-committed statement gets one visibility decision, including every metadata
    /// lookup and all the content it streams: the view keeps the snapshot a refreshing
    /// read-committed context would re-capture on every access. Physical brackets and record
    /// stamps use the view as they use the transaction, because they key by
    /// <see cref="Sequence"/>, which the view shares (the coordinator pairs statement brackets by
    /// sequence). Commit and rollback take the transaction's own context; a manager refuses a
    /// view. Pinning a view pins the same snapshot again.
    /// </para>
    /// <para>
    /// Documents, Graph and Blob each used to wrap the context in an identical private decorator
    /// for this; the decorators were deleted when the context became a sealed type (plan §6.1).
    /// </para>
    /// </remarks>
    public TransactionContext PinStatementSnapshot() => new(this, Snapshot, IsolationLevel, isStatementView: true);

    // The manager holds its registration lock before this lifecycle latch, as for ClaimEnd.
    // Replacement preserves the original begin snapshot and shares end/apply state with every
    // earlier handle. Neither lock spans storage, an engine lock wait, or a coordinator lock.
    internal TransactionContext ReplaceIsolationLevel(IsolationLevel isolationLevel)
    {
        lock (_endSync)
        {
            var owner = _transaction ?? this;
            if ((owner._admission & endingFlag) != 0 || State != TransactionState.Active)
            { throw new TransactionAbortedException(ErrorMessages.IsolationTransactionEnding); }
            if (owner._applying != 0)
            { throw new TransactionAbortedException(ErrorMessages.IsolationStatementRunning); }
            if ((owner._admission & partialUndoFlag) != 0)
            { throw new TransactionUndoException("CK04009", ErrorMessages.PartialUndoPending); }
            return new TransactionContext(this, _beginSnapshot, isolationLevel, isStatementView: false);
        }
    }

    /// <summary>
    /// Claims the end of this context for one commit, rollback or abort. Exactly
    /// one caller wins; from the claim on, no statement apply is admitted. The
    /// winner awaits <see cref="WaitForAppliesAsync"/> before it touches the
    /// writer's versions or log records.
    /// </summary>
    /// <returns>True for the one caller that claimed the end.</returns>
    internal bool TryClaimEnd()
    {
        if (_transaction is not null)
        {
            return _transaction.TryClaimEnd();
        }

        lock (_endSync)
        {
            if ((_admission & endingFlag) != 0)
            {
                return false;
            }

            _admission |= endingFlag;
            return true;
        }
    }

    /// <summary>
    /// Admits one statement apply, unless the context's end is claimed or it is no longer active.
    /// Every admitted apply is paired with <see cref="ExitApply"/>.
    /// </summary>
    /// <returns>True when the apply may run.</returns>
    internal bool TryEnterApply()
    {
        if (_transaction is not null)
        {
            return _transaction.TryEnterApply();
        }

        lock (_endSync)
        {
            if (_admission != 0 || State != TransactionState.Active)
            {
                return false;
            }

            _applying++;
            return true;
        }
    }

    /// <summary>
    /// Ends one admitted statement apply and wakes an end waiting for the last one.
    /// </summary>
    internal void ExitApply()
    {
        if (_transaction is not null)
        {
            _transaction.ExitApply();
            return;
        }

        TaskCompletionSource? drained = null;

        lock (_endSync)
        {
            if (--_applying == 0 && _drained is not null)
            {
                drained = _drained;
                _drained = null;
            }
        }

        drained?.TrySetResult();
    }

    /// <summary>
    /// Waits for every statement apply admitted before the end was claimed. Called by
    /// the end's claimant only, after <see cref="TryClaimEnd"/>; idempotent.
    /// </summary>
    /// <returns>A task that completes when no apply of the context is running.</returns>
    /// <remarks>
    /// The wait observes no token and is bounded: an apply runs inside the
    /// coordinator's apply gate, where nothing it awaits may actually wait (the gate's
    /// own invariant). A started rollback therefore still always completes.
    /// </remarks>
    internal Task WaitForAppliesAsync()
    {
        if (_transaction is not null)
        {
            return _transaction.WaitForAppliesAsync();
        }

        lock (_endSync)
        {
            if (_applying == 0)
            {
                return Task.CompletedTask;
            }

            _drained ??= new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            return _drained.Task;
        }
    }

    private sealed class DebugView
    {
        private readonly global::Assimalign.Cohesion.Database.Transactions.TransactionContext _target;

        public DebugView(global::Assimalign.Cohesion.Database.Transactions.TransactionContext value)
        {
            _target = value;
        }

        public global::Assimalign.Cohesion.Database.Transactions.TransactionId Id => _target.Id;
        public global::Assimalign.Cohesion.Database.Transactions.TransactionSequence Sequence => _target.Sequence;
        public global::Assimalign.Cohesion.Database.Transactions.IsolationLevel IsolationLevel => _target.IsolationLevel;
        public global::Assimalign.Cohesion.Database.Transactions.TransactionState State => _target.State;
    }
}
