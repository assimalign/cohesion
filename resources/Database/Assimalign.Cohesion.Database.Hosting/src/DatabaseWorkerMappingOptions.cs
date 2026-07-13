using System;

namespace Assimalign.Cohesion.Database.Hosting;

/// <summary>
/// Maps the engine-owned worker inventory onto the host's execution menu, one slot
/// per <see cref="DatabaseEngineWorkerKind"/>. Defaults implement the execution-model
/// mapping in docs/DESIGN.md: the latency-critical WAL flusher and the paced page
/// writer on dedicated threads, the periodic checkpoint/maintenance rows on
/// pool-scheduled timers.
/// </summary>
public sealed class DatabaseWorkerMappingOptions
{
    /// <summary>
    /// Gets the slot for the write-ahead-log group-commit flusher. Default:
    /// dedicated thread (every grouped commit waits on it — it must be immune to
    /// thread-pool starvation).
    /// </summary>
    public DatabaseWorkerSlotOptions WriteAheadFlush { get; } = new(DatabaseWorkerExecution.DedicatedThread);

    /// <summary>
    /// Gets the slot for the dirty-page writer. Default: dedicated thread (paced
    /// synchronous I/O owns its thread for its whole life).
    /// </summary>
    public DatabaseWorkerSlotOptions PageWriteBack { get; } = new(DatabaseWorkerExecution.DedicatedThread);

    /// <summary>
    /// Gets the slot for the checkpointer. Default: pool-scheduled timer (periodic,
    /// not latency-critical).
    /// </summary>
    public DatabaseWorkerSlotOptions Checkpoint { get; } = new(DatabaseWorkerExecution.PooledTimer);

    /// <summary>
    /// Gets the slot for MVCC version purge / vacuum. Default: pool-scheduled timer
    /// (bursty, yieldy, low priority).
    /// </summary>
    public DatabaseWorkerSlotOptions VersionPurge { get; } = new(DatabaseWorkerExecution.PooledTimer);

    /// <summary>
    /// Gets the slot for index maintenance. Default: pool-scheduled timer (throttled
    /// below foreground writes).
    /// </summary>
    public DatabaseWorkerSlotOptions IndexMaintenance { get; } = new(DatabaseWorkerExecution.PooledTimer);

    /// <summary>
    /// Resolves the slot for a worker kind.
    /// </summary>
    internal DatabaseWorkerSlotOptions GetSlot(DatabaseEngineWorkerKind kind) => kind switch
    {
        DatabaseEngineWorkerKind.WriteAheadFlush => WriteAheadFlush,
        DatabaseEngineWorkerKind.PageWriteBack => PageWriteBack,
        DatabaseEngineWorkerKind.Checkpoint => Checkpoint,
        DatabaseEngineWorkerKind.VersionPurge => VersionPurge,
        DatabaseEngineWorkerKind.IndexMaintenance => IndexMaintenance,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown engine worker kind."),
    };
}
