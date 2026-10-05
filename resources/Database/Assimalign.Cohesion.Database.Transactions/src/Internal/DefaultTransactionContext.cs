using System.Threading.Tasks;

namespace Assimalign.Cohesion.Database.Transactions.Internal;

/// <summary>
/// Internal transaction context. Under <see cref="IsolationLevel.ReadCommitted"/>
/// the snapshot is re-captured on every access (each statement reads it once);
/// under snapshot and serializable isolation it is fixed at begin.
/// </summary>
/// <remarks>
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
internal sealed class DefaultTransactionContext : ITransactionContext
{
    private readonly TransactionManager _manager;
    private readonly TransactionSnapshot _beginSnapshot;
    private readonly object _endSync = new();
    private int _applying;
    private bool _ending;
    private TaskCompletionSource? _drained;

    internal DefaultTransactionContext(
        TransactionManager manager,
        TransactionId id,
        TransactionSequence sequence,
        IsolationLevel isolationLevel,
        TransactionSnapshot beginSnapshot)
    {
        _manager = manager;
        Id = id;
        Sequence = sequence;
        IsolationLevel = isolationLevel;
        _beginSnapshot = beginSnapshot;
        State = TransactionState.Active;
    }

    /// <summary>
    /// Gets the manager that created this context, so a manager can reject a
    /// context begun on a different manager instance.
    /// </summary>
    internal TransactionManager Manager => _manager;

    /// <inheritdoc />
    public TransactionId Id { get; }

    /// <inheritdoc />
    public TransactionSequence Sequence { get; }

    /// <inheritdoc />
    public IsolationLevel IsolationLevel { get; }

    /// <inheritdoc />
    public TransactionState State { get; internal set; }

    /// <inheritdoc />
    public TransactionSnapshot Snapshot =>
        IsolationLevel == IsolationLevel.ReadCommitted && State == TransactionState.Active
            ? _manager.CaptureSnapshot(Sequence)
            : _beginSnapshot;

    /// <summary>
    /// Claims the end of this context for one commit, rollback or abort. Exactly
    /// one caller wins; from the claim on, no statement apply is admitted. The
    /// winner awaits <see cref="WaitForAppliesAsync"/> before it touches the
    /// writer's versions or log records.
    /// </summary>
    /// <returns>True for the one caller that claimed the end.</returns>
    internal bool TryClaimEnd()
    {
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
