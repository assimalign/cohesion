using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;

using Assimalign.Cohesion.Database.Storage;
using Assimalign.Cohesion.Database.Transactions;

namespace Assimalign.Cohesion.Database.Indexing.Internal;

[DebuggerDisplay("IndexStructureDirectory")]
[DebuggerTypeProxy(typeof(DebugView))]
internal sealed class IndexStructureDirectory
{
    private readonly List<IndexStructure> _structures = [];
    private readonly Dictionary<(ulong Owner, string Name), IndexStructure> _byIdentity = [];
    private readonly Dictionary<IndexStructure, int> _positions = [];
    private readonly object _sync = new();
    private int _position;

    internal void Add(IndexStructure structure)
    {
        lock (_sync)
        {
            _byIdentity.Add((structure.OwnerId, structure.Name), structure);
            _positions.Add(structure, _structures.Count);
            _structures.Add(structure);
        }
    }

    internal void Retire(ulong owner, string name)
    {
        lock (_sync)
        {
            var structure = _byIdentity[(owner, name)];
            structure.Retire();
            int position = _positions[structure];
            var last = _structures[^1];
            _structures[position] = last;
            _positions[last] = position;
            _structures.RemoveAt(_structures.Count - 1);
            _positions.Remove(structure);
            _byIdentity.Remove((owner, name));
        }
    }

    internal long PurgeWriters(StorageTransaction bracket, IReadOnlySet<TransactionSequence> writers, CancellationToken token)
    {
        long purged = 0;
        IndexStructure[] structures;
        lock (_sync) { structures = _structures.ToArray(); }
        foreach (var structure in structures)
        {
            token.ThrowIfCancellationRequested();
            purged += structure.PurgeWriters(bracket, writers);
        }
        return purged;
    }

    internal long Prune(TransactionSequence bound, int pageBudget, CancellationToken token)
    {
        int count;
        lock (_sync) { count = _structures.Count; }
        long removed = 0;
        int completed = 0;
        for (int page = 0; page < pageBudget && completed < count; page++)
        {
            token.ThrowIfCancellationRequested();
            IndexStructure structure;
            lock (_sync)
            {
                if (_structures.Count == 0) { break; }
                _position %= _structures.Count;
                structure = _structures[_position];
            }
            var result = structure.Prune(bound, token);
            removed += result.Removed;
            if (result.Completed)
            {
                lock (_sync) { _position = _structures.Count == 0 ? 0 : (_position + 1) % _structures.Count; }
                completed++;
            }
        }
        return removed;
    }

    internal static void EnsureFormat(IEnumerable<IndexStructure> structures)
    {
        foreach (var structure in structures) { structure.EnsureFormat(); }
    }

    private sealed class DebugView
    {
        private readonly IndexStructureDirectory _target;
        public DebugView(IndexStructureDirectory value) { _target = value; }
        public int Count => _target._structures.Count;
        public int Position => _target._position;
        public IReadOnlyList<IndexStructure> Structures => _target._structures;
    }
}
