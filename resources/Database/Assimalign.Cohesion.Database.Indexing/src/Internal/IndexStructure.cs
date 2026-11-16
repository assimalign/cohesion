using System.Collections.Generic;
using System.Threading;

using Assimalign.Cohesion.Database.Storage;
using Assimalign.Cohesion.Database.Transactions;

namespace Assimalign.Cohesion.Database.Indexing.Internal;

// The variant boundary stays inside Indexing. Public structures remain sealed leaves.
internal abstract class IndexStructure
{
    private protected IndexStructure(ulong ownerId, string name)
    {
        OwnerId = ownerId;
        Name = name;
    }

    internal ulong OwnerId { get; }
    internal string Name { get; }

    internal void EnsureFormat() => EnsureFormatCore();
    internal long PurgeWriters(StorageTransaction bracket, IReadOnlySet<TransactionSequence> writers)
        => PurgeWritersCore(bracket, writers);
    internal (long Removed, bool Completed) Prune(TransactionSequence bound, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        return PruneCore(bound, token);
    }
    internal void Retire() => RetireCore();

    protected abstract void EnsureFormatCore();
    protected abstract long PurgeWritersCore(StorageTransaction bracket, IReadOnlySet<TransactionSequence> writers);
    protected abstract (long Removed, bool Completed) PruneCore(TransactionSequence bound, CancellationToken token);
    // Called under the directory monitor. Adapters may change in-memory state
    // only; no I/O, waits or callbacks into the model are permitted here.
    protected abstract void RetireCore();
}
