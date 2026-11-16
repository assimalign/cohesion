using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;

using Assimalign.Cohesion.Database.Storage;
using Assimalign.Cohesion.Database.Transactions;

namespace Assimalign.Cohesion.Database.Indexing.Internal;

[DebuggerDisplay("BTreeStructure")]
[DebuggerTypeProxy(typeof(DebugView))]
internal sealed class BTreeStructure : IndexStructure
{
    private readonly Storage.Storage _storage;
    private readonly BTreeIndexRegistration _registration;
    private readonly BTreeIndex? _tree;

    internal BTreeStructure(Storage.Storage storage, BTreeIndexRegistration registration, BTreeIndex? tree = null)
        : base(registration.ObjectId, registration.Definition.Name)
    {
        _storage = storage;
        _registration = registration;
        _tree = tree;
    }

    protected override void EnsureFormatCore() => BTreeIndex.EnsureFormat(_storage, _registration);
    protected override long PurgeWritersCore(StorageTransaction bracket, IReadOnlySet<TransactionSequence> writers)
        => _tree!.PurgeWriters(bracket, writers);
    protected override (long Removed, bool Completed) PruneCore(TransactionSequence bound, CancellationToken token)
        => _tree!.PruneLeaf(bound, token);

    // Detached readers keep the tree until the model's horizon permits reclamation.
    protected override void RetireCore() { }

    private sealed class DebugView
    {
        private readonly BTreeStructure _target;
        public DebugView(BTreeStructure value) { _target = value; }
        public ulong OwnerId => _target.OwnerId;
        public string Name => _target.Name;
        public long RootPageId => _target._registration.RootPageId;
        public BTreeIndex? Tree => _target._tree;
    }
}
