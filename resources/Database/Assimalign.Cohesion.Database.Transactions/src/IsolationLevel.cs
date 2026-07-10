namespace Assimalign.Cohesion.Database.Transactions;

/// <summary>
/// The isolation level a transaction executes under.
/// </summary>
/// <remarks>
/// All levels are implemented over MVCC snapshots: readers never block writers.
/// Write-write conflicts are always detected regardless of level.
/// </remarks>
public enum IsolationLevel : byte
{
    /// <summary>
    /// Each statement sees data committed before the statement began.
    /// </summary>
    ReadCommitted = 0,

    /// <summary>
    /// The whole transaction sees a single snapshot taken when it began.
    /// This is the default level.
    /// </summary>
    Snapshot,

    /// <summary>
    /// Snapshot isolation plus serialization-conflict detection: concurrent
    /// transactions whose combined effect is not equivalent to any serial
    /// order abort with a serialization failure.
    /// </summary>
    Serializable,
}
