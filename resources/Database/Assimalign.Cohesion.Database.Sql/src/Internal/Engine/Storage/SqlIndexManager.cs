using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Database.Indexing;
using Assimalign.Cohesion.Database.Storage;
using Assimalign.Cohesion.Database.Transactions;

namespace Assimalign.Cohesion.Database.Sql.Internal;

// SQL owns logical names; Indexing owns physical trees. Kind 14 reserves alternate
// physical owners before building, so abandoning a tree never frees its heap.
[System.Diagnostics.DebuggerDisplay("SqlIndexManager")]
[System.Diagnostics.DebuggerTypeProxy(typeof(DebugView))]
internal sealed class SqlIndexManager
{
    private static readonly IEqualityComparer<(ulong, string)> _keys = EqualityComparer<(ulong, string)>.Create(
        (left, right) => left.Item1 == right.Item1 && StringComparer.OrdinalIgnoreCase.Equals(left.Item2, right.Item2),
        value => HashCode.Combine(value.Item1, StringComparer.OrdinalIgnoreCase.GetHashCode(value.Item2)));
    private readonly BTreeIndexManager _normal;
    private readonly BTreeIndexManagerOptions _options;
    private readonly object _sync = new();
    private readonly Dictionary<(ulong, string), Entry> _alternate = new(_keys);
    private readonly Dictionary<(ulong, string), Entry> _prepared = new(_keys);
    private readonly Dictionary<long, ulong> _owners = new();
    private readonly List<BTreeIndexManager> _maintenance = new();
    private int _prunePosition;

    private SqlIndexManager(BTreeIndexManagerOptions options, IReadOnlySet<ulong> forbiddenOwners, ulong nextIdentity)
    {
        _options = options;
        var normal = new List<BTreeIndexRegistration>();
        var treeOwners = new HashSet<ulong>();
        foreach (var registration in options.ExistingIndexes ?? [])
        {
            using var root = options.Storage.PageManager.GetPage((PageId)registration.RootPageId);
            ulong owner = root.Page.OwnerId;
            if (owner == registration.ObjectId) { normal.Add(registration); }
            else
            {
                if (owner == 0 || owner >= nextIdentity || forbiddenOwners.Contains(owner) || !treeOwners.Add(owner))
                { throw new StorageCorruptionException(root.Id, ErrorMessages.JointIndexShapeInvalid); }
                var manager = CreatePhysical([registration with { ObjectId = owner }]);
                _owners.Add(registration.RootPageId, owner);
                manager.TryGetIndex(owner, registration.Definition.Name, out var tree);
                _alternate.Add(Key(registration.ObjectId, registration.Definition.Name), new Entry(manager, registration, tree));
                _maintenance.Add(manager);
            }
        }
        _normal = BTreeIndexManager.Create(new BTreeIndexManagerOptions
        {
            Storage = options.Storage, TransactionSource = options.TransactionSource,
            LockManager = options.LockManager, PruneBound = options.PruneBound, ExistingIndexes = normal,
        });
        _maintenance.Add(_normal);
    }

    internal static SqlIndexManager Create(BTreeIndexManagerOptions options, IReadOnlySet<ulong> forbiddenOwners, ulong nextIdentity)
        => new(options, forbiddenOwners, nextIdentity);
    private static (ulong, string) Key(ulong table, string name) => (table, name);

    private BTreeIndexManager CreatePhysical(IReadOnlyList<BTreeIndexRegistration>? indexes = null)
        => BTreeIndexManager.Create(new BTreeIndexManagerOptions
        {
            Storage = _options.Storage, TransactionSource = _options.TransactionSource,
            // SQL pre-acquires unique keys by LOGICAL heap identity before apply.
            // Physical owner identities must never introduce a second key-lock
            // namespace or a wait while holding the coordinator's apply gate.
            LockManager = null, PruneBound = _options.PruneBound, ExistingIndexes = indexes,
        });

    internal async ValueTask<BTreeIndex> CreateIndexAsync(TransactionContext transaction, ulong table,
        IndexDefinition definition, CancellationToken token = default)
    {
        var index = await _normal.CreateIndexAsync(transaction, table, definition, token).ConfigureAwait(false);
        return index;
    }

    internal async ValueTask<BTreeIndex> CreatePreparedAsync(TransactionContext transaction, ulong table, ulong owner,
        IndexDefinition definition, CancellationToken token)
    {
        var manager = CreatePhysical();
        var tree = await manager.CreateIndexAsync(transaction, owner, definition, token).ConfigureAwait(false);
        var registration = manager.ExportRegistrations().Single() with { ObjectId = table };
        lock (_sync)
        {
            _prepared.Add(Key(table, definition.Name), new Entry(manager, registration, tree));
            _alternate.EnsureCapacity(_alternate.Count + _prepared.Count);
            _maintenance.EnsureCapacity(_maintenance.Count + _prepared.Count);
            _owners[registration.RootPageId] = owner;
        }
        return tree;
    }

    internal bool TryGetIndex(ulong table, string name, out BTreeIndex tree)
    {
        lock (_sync)
        {
            if (_alternate.TryGetValue(Key(table, name), out var entry)) { tree = entry.Tree; return true; }
            return _normal.TryGetIndex(table, name, out tree!);
        }
    }

    internal bool TryGetPrepared(ulong table, string name, out BTreeIndex tree)
    {
        lock (_sync)
        {
            if (_prepared.TryGetValue(Key(table, name), out var entry)) { tree = entry.Tree; return true; }
            tree = null!;
            return false;
        }
    }

    internal ulong PageOwner(BTreeIndexRegistration registration)
    { lock (_sync) { return _owners.GetValueOrDefault(registration.RootPageId, registration.ObjectId); } }

    internal IReadOnlyList<BTreeIndexRegistration> ExportRegistrations(bool includePrepared = false)
    {
        lock (_sync)
        {
            if (_alternate.Count == 0 && (!includePrepared || _prepared.Count == 0)) { return _normal.ExportRegistrations(); }
            var values = _normal.ExportRegistrations().ToDictionary(value => Key(value.ObjectId, value.Definition.Name), _keys);
            foreach (var pair in _alternate) { values[pair.Key] = pair.Value.Registration; }
            if (includePrepared) { foreach (var pair in _prepared) { values[pair.Key] = pair.Value.Registration; } }
            return values.Values.ToArray();
        }
    }

    // Catalog monitor -> directory monitor; no reverse acquisition or tree/page
    // latch under this monitor. Capacity is reserved before the catalog commit.
    internal void PreparePublication()
    {
        lock (_sync)
        {
            _alternate.EnsureCapacity(_alternate.Count + _prepared.Count);
            _maintenance.EnsureCapacity(_maintenance.Count + _prepared.Count);
        }
    }

    internal void PublishPrepared()
    {
        lock (_sync)
        {
            foreach (var pair in _prepared)
            {
                var registration = pair.Value.Registration;
                if (_alternate.TryGetValue(pair.Key, out var old))
                { _maintenance.Remove(old.Manager); _owners.Remove(old.Registration.RootPageId); }
                _alternate[pair.Key] = pair.Value;
                _maintenance.Add(pair.Value.Manager);
            }
            _prepared.Clear();
        }
    }

    internal void DiscardPrepared()
    {
        lock (_sync)
        {
            foreach (var entry in _prepared.Values) { _owners.Remove(entry.Registration.RootPageId); }
            _prepared.Clear();
        }
    }

    internal ValueTask DropIndexAsync(TransactionContext transaction, ulong table, string name, CancellationToken token = default)
    {
        lock (_sync)
        {
            token.ThrowIfCancellationRequested();
            if (_alternate.Remove(Key(table, name), out var removed))
            {
                ulong owner = _owners.GetValueOrDefault(removed.Registration.RootPageId, table);
                _maintenance.Remove(removed.Manager);
                _owners.Remove(removed.Registration.RootPageId);
                return removed.Manager.DropIndexAsync(transaction, owner, name, token);
            }
            return _normal.DropIndexAsync(transaction, table, name, token);
        }
    }

    internal ValueTask<long> PruneAsync(TransactionSequence bound, CancellationToken token)
    {
        BTreeIndexManager manager;
        lock (_sync)
        {
            _prunePosition %= _maintenance.Count;
            manager = _maintenance[_prunePosition++];
        }
        // The version-purge worker lane finishes pruning before running table
        // reclamation. Directory changes may detach this manager meanwhile; its
        // pages remain fenced until that lane can reclaim them. One manager per
        // call preserves the global 64-leaf maintenance bound.
        return manager.PruneAsync(bound, token);
    }

    internal async ValueTask<long> PurgeWritersAsync(StorageTransaction bracket, IReadOnlySet<TransactionSequence> writers, CancellationToken token)
    {
        long count = 0;
        foreach (var manager in _maintenance) { count += await manager.PurgeWritersAsync(bracket, writers, token).ConfigureAwait(false); }
        return count;
    }

    private sealed record Entry(BTreeIndexManager Manager, BTreeIndexRegistration Registration, BTreeIndex Tree);

    private sealed class DebugView
    {
        public DebugView(SqlIndexManager value) { _ = value; }
    }
}
