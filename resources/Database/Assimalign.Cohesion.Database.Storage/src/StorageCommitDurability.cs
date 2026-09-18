namespace Assimalign.Cohesion.Database.Storage;

/// <summary>
/// How a storage transaction's commit reaches stable storage. Durable modes wait
/// for their journal records to become durable; non-durable storage makes no such
/// guarantee. All modes append the same commit records and preserve visibility.
/// </summary>
public enum StorageCommitDurability : byte
{
    /// <summary>
    /// Each commit performs its own durable journal flush before returning. The
    /// default: simplest latency profile, one fsync per commit.
    /// </summary>
    Synchronous = 0,

    /// <summary>
    /// Commits register with the group-commit gate and wait (bounded by
    /// <see cref="Storage.GroupCommitWindow"/>) for a flush worker's durable flush
    /// that covers every pending commit, so concurrent commits share one fsync. A
    /// commit whose window lapses without a worker flush performs the flush inline
    /// itself — durability is never weakened, only batched.
    /// </summary>
    Grouped = 1,

    /// <summary>
    /// Commits do not flush to durable storage because the backing store cannot
    /// provide it. Commit records, transaction visibility, and rollback semantics
    /// are unchanged, but no persistence across a crash is promised.
    /// </summary>
    None = 2,
}
