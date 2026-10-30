using System;
using System.Collections.Generic;

namespace Assimalign.Cohesion.Database.Sql.Catalog;

public sealed partial class SqlCatalog
{
    private readonly Dictionary<ulong, SqlCatalogIndex[]> _planningIndexes = new();

    internal IReadOnlyList<SqlCatalogIndex> GetPlanningIndexes(ulong tableObjectId)
    {
        lock (_sync)
        {
            ThrowIfOffline();
            return _planningIndexes.TryGetValue(tableObjectId, out var indexes) ? indexes : Array.Empty<SqlCatalogIndex>();
        }
    }

    private void SetIndexSlot(IndexSlot slot)
    {
        var index = slot.Index;
        _indexes[(index.TableObjectId, index.Name)] = slot;
        _planningIndexes.TryGetValue(index.TableObjectId, out var previous);
        previous ??= [];
        int position = Array.FindIndex(previous, item => string.Equals(item.Name, index.Name, StringComparison.OrdinalIgnoreCase));
        var next = new SqlCatalogIndex[previous.Length + (position < 0 ? 1 : 0)];
        previous.CopyTo(next, 0);
        next[position < 0 ? previous.Length : position] = index;
        Array.Sort(next, static (left, right) => StringComparer.OrdinalIgnoreCase.Compare(left.Name, right.Name));
        _planningIndexes[index.TableObjectId] = next;
    }

    private void RemoveIndexSlot((ulong TableObjectId, string Name) key)
    {
        if (!_indexes.Remove(key)) { return; }
        var previous = _planningIndexes[key.TableObjectId];
        if (previous.Length == 1) { _planningIndexes.Remove(key.TableObjectId); return; }
        var next = new SqlCatalogIndex[previous.Length - 1];
        int destination = 0;
        foreach (var index in previous)
        { if (!string.Equals(index.Name, key.Name, StringComparison.OrdinalIgnoreCase)) { next[destination++] = index; } }
        _planningIndexes[key.TableObjectId] = next;
    }

    // Planning's ordinary join fast path must not allocate GetIndexes' result list
    // merely to discover that there is no partial index. No metadata is exposed.
    internal bool ContainsPartialIndex(ulong tableObjectId)
    {
        lock (_sync)
        {
            ThrowIfOffline();
            foreach (var entry in _indexes)
            {
                if (entry.Value.Index.TableObjectId == tableObjectId && entry.Value.Index.Predicate is not null) { return true; }
            }
            return false;
        }
    }
}
