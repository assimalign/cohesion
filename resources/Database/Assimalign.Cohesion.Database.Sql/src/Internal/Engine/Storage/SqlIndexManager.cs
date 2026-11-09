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
    private readonly Func<ulong, CancellationToken, ulong> _reserveOwner;
    private readonly Action<ulong> _abandonOwner;
    private readonly Func<ulong, bool> _retiringOwner;
    private readonly BTreeIndexManagerOptions _options;
    private readonly object _sync = new();
    private readonly Dictionary<(ulong, string), Entry> _alternate = new(_keys);
    private readonly Dictionary<(ulong, string), Entry> _prepared = new(_keys);
    private readonly Dictionary<long, ulong> _owners = new();
    private readonly List<(BTreeIndexManager Manager, ulong Owner)> _maintenance = new();
    private int _prunePosition;

    private SqlIndexManager(BTreeIndexManagerOptions options, IReadOnlySet<ulong> forbiddenOwners, ulong nextIdentity,
        Func<ulong, CancellationToken, ulong> reserveOwner, Action<ulong> abandonOwner,
        Func<BTreeIndexRegistration, ulong> expectedOwner, Func<ulong, bool> retiringOwner)
    {
        _reserveOwner = reserveOwner;
        _abandonOwner = abandonOwner;
        _retiringOwner = retiringOwner;
        _options = options;
        var treeOwners = new HashSet<ulong>();
        foreach (var registration in options.ExistingIndexes ?? [])
        {
            using var root = options.Storage.PageManager.GetPage((PageId)registration.RootPageId);
            ulong owner = root.Page.OwnerId;
            if (owner != expectedOwner(registration) || owner == registration.ObjectId) { throw new StorageCorruptionException(root.Id, ErrorMessages.JointIndexShapeInvalid); }
            else
            {
                if (owner == 0 || owner >= nextIdentity || forbiddenOwners.Contains(owner) || !treeOwners.Add(owner))
                { throw new StorageCorruptionException(root.Id, ErrorMessages.JointIndexShapeInvalid); }
                var manager = CreatePhysical([registration with { ObjectId = owner }]);
                _owners.Add(registration.RootPageId, owner);
                manager.TryGetIndex(owner, registration.Definition.Name, out var tree);
                _alternate.Add(Key(registration.ObjectId, registration.Definition.Name), new Entry(manager, registration, tree));
                _maintenance.Add((manager, owner));
            }
        }
    }

    internal static SqlIndexManager Create(BTreeIndexManagerOptions options, IReadOnlySet<ulong> forbiddenOwners, ulong nextIdentity,
        Func<ulong, CancellationToken, ulong> reserveOwner, Action<ulong> abandonOwner,
        Func<BTreeIndexRegistration, ulong> expectedOwner, Func<ulong, bool> retiringOwner)
        => new(options, forbiddenOwners, nextIdentity, reserveOwner, abandonOwner, expectedOwner, retiringOwner);
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
        IndexDefinition definition, CancellationToken token = default, ulong physicalOwner = 0)
    {
        ulong owner = physicalOwner == 0 ? _reserveOwner(table, token) : physicalOwner;
        try
        {
            var manager = CreatePhysical();
            var tree = await manager.CreateIndexAsync(transaction, owner, definition, token).ConfigureAwait(false);
            var registration = manager.ExportRegistrations().Single() with { ObjectId = table };
            lock (_sync)
            {
                _alternate.Add(Key(table, definition.Name), new Entry(manager, registration, tree));
                _maintenance.Add((manager, owner));
                _owners.Add(registration.RootPageId, owner);
            }
            return tree;
        }
        catch { _abandonOwner(owner); throw; }
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
            tree = null!;
            return false;
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

    internal bool IsKnownRegistration(BTreeIndexRegistration registration)
    {
        lock (_sync)
        {
            var key = Key(registration.ObjectId, registration.Definition.Name);
            return _alternate.TryGetValue(key, out var live) && Matches(live, registration) ||
                _prepared.TryGetValue(key, out var prepared) && Matches(prepared, registration);
        }
        static bool Matches(Entry entry, BTreeIndexRegistration registration)
            => entry.Registration.RootPageId == registration.RootPageId &&
                entry.Registration.Definition.Kind == registration.Definition.Kind &&
                entry.Registration.Definition.IsUnique == registration.Definition.IsUnique;
    }

    internal IReadOnlyList<BTreeIndexRegistration> ExportRegistrations(bool includePrepared = false)
    {
        lock (_sync)
        {
            var values = new Dictionary<(ulong, string), BTreeIndexRegistration>(_keys);
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
                { _maintenance.Remove((old.Manager, _owners[old.Registration.RootPageId])); _owners.Remove(old.Registration.RootPageId); }
                _alternate[pair.Key] = pair.Value;
                _maintenance.Add((pair.Value.Manager, _owners[registration.RootPageId]));
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
        Entry? removed;
        ulong owner = 0;
        lock (_sync)
        {
            token.ThrowIfCancellationRequested();
            if (_alternate.Remove(Key(table, name), out removed))
            {
                owner = _owners.GetValueOrDefault(removed.Registration.RootPageId);
                _maintenance.Remove((removed.Manager, owner));
                _owners.Remove(removed.Registration.RootPageId);
            }
        }
        if (removed is null) { return default; }
        // Catalog -> directory is the publication lock order. Never call back into
        // the catalog while holding the directory monitor, including failure cleanup.
        if (owner == 0) { return default; }
        _abandonOwner(owner);
        return removed.Manager.DropIndexAsync(transaction, owner, name, token);
    }

    internal ValueTask<long> PruneAsync(TransactionSequence bound, CancellationToken token)
    {
        const int pageBudget = 64;
        int checks;
        lock (_sync) { checks = Math.Min(pageBudget, _maintenance.Count); }
        int remaining = pageBudget;
        long removed = 0;
        for (int check = 0; check < checks && remaining > 0; check++)
        {
            token.ThrowIfCancellationRequested();
            (BTreeIndexManager Manager, ulong Owner) selected;
            lock (_sync)
            {
                if (_maintenance.Count == 0) { break; }
                _prunePosition %= _maintenance.Count;
                selected = _maintenance[_prunePosition];
            }
            // A public catalog-only DROP can leave a stale directory handle. Its
            // receipt owns maintenance now, including when the root was in the first
            // reclaimed batch. Call back only after releasing the directory monitor.
            if (_retiringOwner(selected.Owner))
            { lock (_sync) { _prunePosition++; } continue; }
            // Index pages include branches, so this is a conservative leaf-work grant.
            // Count without copying/loading the directory. A large tree keeps the next turn
            // when it cannot fit the remaining grant; small trees share the same 64 leaves.
            int grant = Math.Min(pageBudget, _options.Storage.GetOwnerIndexPageCount(selected.Owner));
            if (grant > remaining) { break; }
            if (grant != 0)
            {
                removed += selected.Manager.PruneAsync(bound, token).GetAwaiter().GetResult();
                remaining -= grant;
            }
            lock (_sync) { _prunePosition++; }
        }
        return new ValueTask<long>(removed);
    }

    internal async ValueTask<long> PurgeWritersAsync(StorageTransaction bracket, IReadOnlySet<TransactionSequence> writers, CancellationToken token)
    {
        long count = 0;
        foreach (var item in _maintenance)
        {
            if (_retiringOwner(item.Owner) || _options.Storage.GetOwnerIndexPageCount(item.Owner) == 0) { continue; }
            count += await item.Manager.PurgeWritersAsync(bracket, writers, token).ConfigureAwait(false);
        }
        return count;
    }

    private sealed record Entry(BTreeIndexManager Manager, BTreeIndexRegistration Registration, BTreeIndex Tree);

    private sealed class DebugView
    {
        public DebugView(SqlIndexManager value) { _ = value; }
    }
}
