namespace Assimalign.Cohesion.Database.Storage;

/// <summary>
/// How a storage transaction's commit reaches stable storage. In both modes a commit
/// is acknowledged only after its journal records are durable — the modes differ in
/// <em>who</em> performs the durable flush, never in the guarantee.
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
    Grouped,
}
