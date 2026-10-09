using System.Threading.Tasks;

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
public sealed class TransactionContext
{
    private readonly TransactionManager _manager;
    private readonly TransactionSnapshot _beginSnapshot;

    // The transaction's own context when this one is a statement view (PinStatementSnapshot),
    // null for the context the manager begins. The end flag, the apply count and the drain
    // signal below live on the transaction's own context only: a view forwards every member
    // that touches them, so one lock always guards one set of state.
    private readonly TransactionContext? _transaction;
    private readonly object _endSync;
    private TransactionState _state;
    private int _applying;
    private bool _ending;
    private TaskCompletionSource? _drained;

    internal TransactionContext(
        TransactionManager manager,
        TransactionId id,
        TransactionSequence sequence,
        IsolationLevel isolationLevel,
        TransactionSnapshot beginSnapshot)
    {
        _manager = manager;
        _endSync = new object();
        Id = id;
        Sequence = sequence;
        IsolationLevel = isolationLevel;
        _beginSnapshot = beginSnapshot;
        _state = TransactionState.Active;
    }

    // A statement view of the transaction's own context: everything but the snapshot is the
    // transaction's, and the snapshot is the one given.
    private TransactionContext(TransactionContext transaction, TransactionSnapshot snapshot)
    {
        _manager = transaction._manager;
        _transaction = transaction;
        _endSync = transaction._endSync;
        Id = transaction.Id;
        Sequence = transaction.Sequence;
        IsolationLevel = transaction.IsolationLevel;
        _beginSnapshot = snapshot;
    }

    /// <summary>
    /// Gets the manager that created this context, so a manager can reject a
    /// context begun on a different manager instance.
    /// </summary>
    internal TransactionManager Manager => _manager;

    /// <summary>
    /// Gets the snapshot fixed when the manager began the transaction (a statement view's pinned
    /// snapshot). Every snapshot the transaction captures later, and every statement view pinned
    /// from it, has a <see cref="TransactionSnapshot.Minimum"/> at or above this one's: a sequence
    /// active then is still active or has ended, and one assigned since is higher than the
    /// transaction's own. The manager's prune bound reads it, so a read-committed transaction's
    /// statement views stay covered while their transaction is active.
    /// </summary>
    internal TransactionSnapshot BeginSnapshot => _beginSnapshot;

    /// <summary>
    /// Gets whether this context is a statement view (<see cref="PinStatementSnapshot"/>) rather
    /// than the context the manager began. A manager ends only its own contexts, never a view.
    /// </summary>
    internal bool IsStatementView => _transaction is not null;

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
        internal set => _state = value;
    }

    /// <summary>
    /// Gets the visibility snapshot reads resolve through. Under
    /// <see cref="IsolationLevel.ReadCommitted"/> the snapshot is refreshed per
    /// statement; under snapshot and serializable isolation it is fixed at begin. A statement
    /// view's snapshot is the one fixed when it was pinned.
    /// </summary>
    public TransactionSnapshot Snapshot =>
        _transaction is null && IsolationLevel == IsolationLevel.ReadCommitted && _state == TransactionState.Active
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
    public TransactionContext PinStatementSnapshot() => new(_transaction ?? this, Snapshot);

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
            if (_ending)
            {
                return false;
            }

            _ending = true;
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
            if (_ending || State != TransactionState.Active)
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
}
