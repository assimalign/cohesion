using System;
using System.Collections.Generic;
using System.Linq;

namespace Assimalign.Cohesion.Database.Sql.Catalog;

/// <summary>
/// An engine-internal, immutable directory captured under the catalog's publication
/// lock. It carries no storage identity and adds no capability to ISqlCatalog.
/// </summary>
internal sealed class SqlCatalogSnapshot
{
    internal static SqlCatalogSnapshot Empty { get; } = new([], []);

    private readonly IReadOnlyDictionary<ulong, IReadOnlyList<SqlCatalogIndex>> _indexes;

    internal SqlCatalogSnapshot(IEnumerable<SqlCatalogTable> tables, IEnumerable<SqlCatalogIndex> indexes)
    {
        Tables = Array.AsReadOnly(tables.ToArray());
        _indexes = indexes.GroupBy(index => index.TableObjectId).ToDictionary(
            group => group.Key, group => (IReadOnlyList<SqlCatalogIndex>)Array.AsReadOnly(group.ToArray()));
    }

    internal IReadOnlyList<SqlCatalogTable> Tables { get; }

    internal IReadOnlyList<SqlCatalogIndex> GetIndexes(ulong objectId)
        => _indexes.TryGetValue(objectId, out var indexes) ? indexes : Array.Empty<SqlCatalogIndex>();

    internal bool TryGetTable(string schema, string name, out SqlCatalogTable table)
    {
        foreach (var candidate in Tables)
        {
            if (string.Equals(candidate.Schema, schema, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(candidate.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                table = candidate;
                return true;
            }
        }

        table = null!;
        return false;
    }
}
