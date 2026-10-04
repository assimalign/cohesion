using System.Threading.Tasks;

namespace Assimalign.Cohesion.Database.Transactions.Internal;

/// <summary>
/// Internal transaction context. Under <see cref="IsolationLevel.ReadCommitted"/>
/// the snapshot is re-captured on every access (each statement reads it once);
/// under snapshot and serializable isolation it is fixed at begin.
/// </summary>
/// <remarks>
/// The context also admits its statement applies. Once an end has begun (commit,
/// rollback or abort), it admits no further apply, and the end waits for the applies
/// already running. A rollback therefore undoes a ledger no later bracket can add to,
/// and no bracket stamped with the sequence lands after the transaction ended (Neo4j
/// stops a terminated transaction's lock client the same way: it marks the client
/// stopped, waits for the operations in flight, then releases its locks,
/// <c>community/lock/.../forseti/ForsetiClient.java:578-593</c>).
/// </remarks>
internal sealed class DefaultTransactionContext : ITransactionContext
{
    private readonly DefaultTransactionManager _manager;
    private readonly TransactionSnapshot _beginSnapshot;
    private readonly object _applySync = new();
    private int _applying;
    private bool _ending;
    private TaskCompletionSource? _drained;

    internal DefaultTransactionContext(
        DefaultTransactionManager manager,
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
    internal DefaultTransactionManager Manager => _manager;

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
    /// Admits one statement apply, unless the context's end has begun or it is no longer active.
    /// Every admitted apply is paired with <see cref="ExitApply"/>.
    /// </summary>
    /// <returns>True when the apply may run.</returns>
    internal bool TryEnterApply()
    {
        lock (_applySync)
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

        lock (_applySync)
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
    /// Begins the context's end: no further apply is admitted, and the returned task
    /// completes once every apply already admitted has exited. Idempotent.
    /// </summary>
    /// <returns>A task that completes when no apply of the context is running.</returns>
    /// <remarks>
    /// The wait is bounded: an apply runs inside the coordinator's apply gate, where
    /// nothing it awaits may actually wait (the gate's own invariant).
    /// </remarks>
    internal Task BeginEndAsync()
    {
        lock (_applySync)
        {
            _ending = true;

            if (_applying == 0)
            {
                return Task.CompletedTask;
            }

            _drained ??= new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            return _drained.Task;
        }
    }
}
