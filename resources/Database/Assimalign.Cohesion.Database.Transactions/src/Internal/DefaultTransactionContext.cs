namespace Assimalign.Cohesion.Database.Transactions;

/// <summary>
/// Internal transaction context. Under <see cref="IsolationLevel.ReadCommitted"/>
/// the snapshot is re-captured on every access (each statement reads it once);
/// under snapshot and serializable isolation it is fixed at begin.
/// </summary>
internal sealed class DefaultTransactionContext : ITransactionContext
{
    private readonly DefaultTransactionManager _manager;
    private readonly TransactionSnapshot _beginSnapshot;

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
}
