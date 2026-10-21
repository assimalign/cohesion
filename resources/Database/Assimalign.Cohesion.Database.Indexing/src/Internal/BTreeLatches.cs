using System.Collections.Concurrent;
using System.Runtime.CompilerServices;

using Assimalign.Cohesion.Database.Storage;

namespace Assimalign.Cohesion.Database.Indexing.Internal;

// Managers are replaceable directories; latch identity belongs to the physical tree.
// The weak storage key retains every root's latch for exactly that storage's lifetime,
// including roots that disappear temporarily during failed DDL or bracket rollback.
internal static class BTreeLatches
{
    private static readonly ConditionalWeakTable<Storage.Storage, ConcurrentDictionary<long, StoragePageLatch>> _storages = new();

    internal static StoragePageLatch Get(Storage.Storage storage, long rootPageId)
        => _storages.GetValue(storage, static _ => new()).GetOrAdd(rootPageId, static _ => StoragePageLatch.Create());
}
