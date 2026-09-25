using System;
using System.Collections.Generic;
using System.Linq;

using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Sql.Catalog.Internal;

/// <summary>
/// An immutable directory captured under the catalog's publication lock.
/// Storage identity and mutable catalog implementation remain private.
/// </summary>
internal sealed class SqlCatalogSnapshot : ISqlCatalogSnapshot
{
    private readonly IReadOnlyDictionary<ulong, IReadOnlyList<SqlCatalogIndex>> _indexes;

    internal SqlCatalogSnapshot(IEnumerable<SqlCatalogTable> tables, IEnumerable<SqlCatalogIndex> indexes, Collation? defaultCollation = null)
    {
        DefaultCollation = defaultCollation ?? Collation.Binary;
        Tables = Array.AsReadOnly(tables.ToArray());
        _indexes = indexes.GroupBy(index => index.TableObjectId).ToDictionary(
            group => group.Key, group => (IReadOnlyList<SqlCatalogIndex>)Array.AsReadOnly(group.ToArray()));
    }

    /// <inheritdoc />
    public IReadOnlyList<SqlCatalogTable> Tables { get; }

    /// <inheritdoc />
    public Collation DefaultCollation { get; }

    /// <inheritdoc />
    public IReadOnlyList<SqlCatalogIndex> GetIndexes(ulong objectId)
        => _indexes.TryGetValue(objectId, out var indexes) ? indexes : Array.Empty<SqlCatalogIndex>();

    /// <inheritdoc />
    public bool TryGetTable(string schema, string name, out SqlCatalogTable table)
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
