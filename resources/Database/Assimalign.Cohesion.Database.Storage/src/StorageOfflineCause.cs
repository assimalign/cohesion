namespace Assimalign.Cohesion.Database.Storage;

/// <summary>
/// What failed and took a storage offline (<see cref="StorageOfflineException.Cause"/>).
/// </summary>
/// <remarks>
/// <para>
/// Whatever the cause, the storage writes nothing more to its journal or its data file until it is
/// reopened, and the reopen's recovery reads the journal and decides the outcome of every commit
/// that was not confirmed.
/// </para>
/// <para>
/// The first three causes are the storage's own: a device operation failed in a way no retry may
/// repair, and the cause tells an operator which one to investigate. The other five are its
/// engine's: a background worker's work on the storage kept failing past the engine's limit, or the
/// journal grew past the engine's hard cap while its checkpoints kept failing, so the engine gave
/// up on the storage (owner decision 25 of 2026-10-06) and took it offline through
/// <see cref="Storage.TakeOffline(StorageOfflineCause, string, Exception)"/>. Each names the
/// worker whose work failed, or the journal cap, so an operator knows which work stopped.
/// </para>
/// </remarks>
public enum StorageOfflineCause : byte
{
    /// <summary>
    /// Getting the journal's records onto its file failed: a durable flush (an fsync) of the
    /// journal (#1243), a drain of its append buffer to the file (#1252), or a flush of the file
    /// that was not durable. Each leaves the journal's tail on the media unknown, so all of them
    /// take the storage offline alike; the storage's message names the exact operation.
    /// </summary>
    JournalFlush = 0,

    /// <summary>
    /// A durable flush of the data file failed (#1243).
    /// </summary>
    DataFlush,

    /// <summary>
    /// A write of the file header failed after its header slot write was issued (#1268): the slot
    /// may already be the newest generation on the media, so no header write may run again, and
    /// without one no checkpoint can truncate the journal.
    /// </summary>
    HeaderWrite,

    /// <summary>
    /// The engine's checkpoints of the storage kept failing: its checkpoint worker failed on the
    /// storage's database as many times in a row as the engine's worker failure limit allows (owner
    /// decision 25). The journal is no longer truncated, so it would grow until the device fills.
    /// </summary>
    CheckpointFailures,

    /// <summary>
    /// The engine's page write-backs of the storage kept failing: its page write-back worker failed
    /// on the storage's database as many times in a row as the engine's worker failure limit allows
    /// (owner decision 25).
    /// </summary>
    PageWriteBackFailures,

    /// <summary>
    /// The engine's write-ahead flushes of the storage kept failing: its group-commit flush worker
    /// failed on the storage's database as many times in a row as the engine's worker failure limit
    /// allows (owner decision 25). A flush whose failure leaves the journal's tail unknown takes
    /// the storage offline at once with <see cref="JournalFlush"/> instead.
    /// </summary>
    WriteAheadFlushFailures,

    /// <summary>
    /// The engine's version purge of the storage kept failing: its version-purge worker, which
    /// retries the undo of a rolled-back writer and reclaims unreachable versions, failed on the
    /// storage's database as many times in a row as the engine's worker failure limit allows (owner
    /// decision 25). A writer whose undo keeps failing keeps its locks until the undo completes.
    /// </summary>
    VersionPurgeFailures,

    /// <summary>
    /// The journal grew past its engine's hard cap while the engine's checkpoints of the storage
    /// kept failing (owner decision 25): the engine stopped it before the journal filled the device.
    /// </summary>
    JournalSizeLimit,
}
