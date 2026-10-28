using System;
using System.Collections.Generic;

using Assimalign.Cohesion.Database.Graph.Protocol;

namespace Assimalign.Cohesion.Database.Graph.Internal;

/// <summary>Value-keyed statement changes for one label/property index.</summary>
[System.Diagnostics.DebuggerDisplay("GraphOverlayIndex")]
[System.Diagnostics.DebuggerTypeProxy(typeof(GraphOverlayIndex.DebugView))]
internal sealed class GraphOverlayIndex
{
    // Equality agrees with residual predicates, including equal values with different numeric tags.
    // NULL is not a seek equality and missing/NULL properties have no overlay posting.
    private readonly Dictionary<object, HashSet<ulong>> _added = new(GraphValueComparer.Instance);
    private readonly Dictionary<object, HashSet<ulong>> _removed = new(GraphValueComparer.Instance);

    internal HashSet<ulong>? Added(object? value) => value is not null && _added.TryGetValue(value, out var ids) ? ids : null;
    internal HashSet<ulong>? Removed(object? value) => value is not null && _removed.TryGetValue(value, out var ids) ? ids : null;

    internal void Replace(string label, string key, GraphNode? previous, GraphNode? current,
        bool original, Action<long> charge)
    {
        if (Value(previous) is { } before)
        {
            if (original) { Add(_removed, before, previous!.Value.Id.Value); }
            else if (_added.TryGetValue(before, out var ids)) { ids.Remove(previous!.Value.Id.Value); }
        }
        if (Value(current) is { } after) { Add(_added, after, current!.Value.Id.Value); }

        object? Value(GraphNode? node)
        {
            if (node is not { } entity || !System.Linq.Enumerable.Contains(entity.Labels, label, StringComparer.Ordinal)) { return null; }
            return entity.Properties.TryGetValue(key, out var value) ? value : null;
        }
        void Add(Dictionary<object, HashSet<ulong>> entries, object value, ulong id)
        {
            if (!entries.TryGetValue(value, out var ids))
            {
                charge(192 + GraphValueMemory.Shallow(value));
                entries.Add(value, ids = []);
            }
            if (!ids.Contains(id))
            {
                // Removed slots are reused. Charge capacity growth, so toggling one node's
                // value cannot accumulate fictitious retained postings on every assignment.
                int capacity = ids.EnsureCapacity(0);
                int grown = ids.EnsureCapacity(ids.Count + 1);
                charge(64L * (grown - capacity));
                ids.Add(id);
            }
        }
    }

    private sealed class DebugView
    {
        public DebugView(GraphOverlayIndex value) { _ = value; }
    }
}
