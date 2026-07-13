namespace Assimalign.Cohesion.Database;

/// <summary>
/// Identifies the role of an engine-owned background worker
/// (<see cref="IDatabaseEngineWorker"/>) so a host can map each worker onto its
/// execution model without knowing the engine's concrete types.
/// </summary>
public enum DatabaseEngineWorkerKind : byte
{
    /// <summary>
    /// The write-ahead-log group-commit flusher: batches commit durability so
    /// concurrent commits share one durable flush. Latency-critical — every commit
    /// in the grouped durability mode waits on it.
    /// </summary>
    WriteAheadFlush = 0,

    /// <summary>
    /// The dirty-page writer: paced write-back of buffered pages between
    /// checkpoints, honoring the write-ahead rule (the journal is made durable past
    /// a page's LSN before the page reaches the data file).
    /// </summary>
    PageWriteBack,

    /// <summary>
    /// The checkpointer: periodically flushes all page state durably and truncates
    /// the journal (with continued LSNs) so recovery stays fast and the journal
    /// stays bounded.
    /// </summary>
    Checkpoint,

    /// <summary>
    /// The MVCC version purge / vacuum: drains version chains that no active
    /// snapshot can see.
    /// </summary>
    VersionPurge,

    /// <summary>
    /// Index maintenance: tombstone vacuum and page merges, throttled below
    /// foreground writes.
    /// </summary>
    IndexMaintenance,
}
