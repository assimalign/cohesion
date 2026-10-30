using System;

namespace Assimalign.Cohesion.Database.Sql.Catalog;

public sealed partial class SqlCatalog
{
    // A negative order probe must not allocate a GetIndexes list on existing sorted
    // queries. This is metadata-only; prefix restrictions and collation bind in planning.
    internal bool ContainsKeyOrder(SqlCatalogTable table, ReadOnlySpan<int> ordinals, ReadOnlySpan<int> orders, ReadOnlySpan<int> fixedColumns)
    {
        lock (_sync)
        {
            ThrowIfOffline();
            foreach (var slot in _indexes.Values)
            {
                if (slot.Index.TableObjectId == table.ObjectId && HasKeyOrder(slot.Index, table, ordinals, orders, fixedColumns)) { return true; }
            }
            return false;
        }
    }

    internal static bool HasKeyOrder(SqlCatalogIndex index, SqlCatalogTable table, ReadOnlySpan<int> ordinals, ReadOnlySpan<int> orders, ReadOnlySpan<int> fixedColumns)
    {
        for (int start = 0; start + ordinals.Length <= index.ColumnNames.Count; start++)
        {
            // The planner's fixed-column mask is deliberately permissive. Actual seek
            // admission still validates conversions, collations and bound values later.
            if (start > 0)
            {
                bool fixedKey = false;
                for (int column = 0; column < fixedColumns.Length; column++)
                {
                    if (fixedColumns[column] != 0 && string.Equals(index.ColumnNames[start - 1], table.Columns[column].Name, StringComparison.OrdinalIgnoreCase))
                    { fixedKey = true; break; }
                }
                if (!fixedKey) { break; }
            }
            bool forward = true, backward = true;
            for (int i = 0; i < ordinals.Length; i++)
            {
                if (!string.Equals(index.ColumnNames[start + i], table.Columns[ordinals[i]].Name, StringComparison.OrdinalIgnoreCase))
                { forward = backward = false; break; }
                forward &= (int)index.KeyOrders[start + i] == orders[i];
                backward &= ((int)index.KeyOrders[start + i] ^ 3) == orders[i];
            }
            if (forward || backward) { return true; }
        }
        return false;
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
