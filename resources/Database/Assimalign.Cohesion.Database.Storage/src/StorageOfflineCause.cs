namespace Assimalign.Cohesion.Database.Storage;

/// <summary>
/// What failed and took a storage offline (<see cref="StorageOfflineException.Cause"/>).
/// </summary>
/// <remarks>
/// Whatever the cause, the storage writes nothing more to its journal or its data file until it is
/// reopened, and the reopen's recovery reads the journal and decides the outcome of every commit
/// that was not confirmed. The cause tells an operator which device operation to investigate.
/// </remarks>
public enum StorageOfflineCause : byte
{
    /// <summary>
    /// A durable flush (an fsync) of the journal failed (#1243).
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
}
