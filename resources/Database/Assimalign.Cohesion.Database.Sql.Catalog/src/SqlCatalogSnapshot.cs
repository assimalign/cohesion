using System;
using System.Collections.Generic;
using System.Linq;

using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Sql.Catalog;

/// <summary>
/// A consistent, read-only capture of a SQL catalog's table and index descriptions, taken by
/// <see cref="SqlCatalog.CaptureSnapshot"/>.
/// </summary>
/// <remarks>
/// All descriptions and the default collation are captured together under the catalog's
/// publication lock. Later catalog publications do not change this capture; it owns no storage
/// or disposal lifetime. <b>Shape (concrete-types plan, phase 4, #1260):</b> the former
/// <c>ISqlCatalogSnapshot</c> interface and its internal implementation collapsed into this
/// sealed type, whose constructor is internal.
/// </remarks>
public sealed class SqlCatalogSnapshot
{
    private readonly IReadOnlyDictionary<ulong, IReadOnlyList<SqlCatalogIndex>> _indexes;

    internal SqlCatalogSnapshot(IEnumerable<SqlCatalogTable> tables, IEnumerable<SqlCatalogIndex> indexes, Collation? defaultCollation = null)
    {
        DefaultCollation = defaultCollation ?? Collation.Binary;
        Tables = Array.AsReadOnly(tables.ToArray());
        _indexes = indexes.GroupBy(index => index.TableObjectId).ToDictionary(
            group => group.Key, group => (IReadOnlyList<SqlCatalogIndex>)Array.AsReadOnly(group.ToArray()));
    }

    /// <summary>Gets the table descriptions present at capture time.</summary>
    public IReadOnlyList<SqlCatalogTable> Tables { get; }

    /// <summary>Gets the database default collation at capture time.</summary>
    public Collation DefaultCollation { get; }

    /// <summary>Gets the captured index descriptions for a table.</summary>
    /// <param name="objectId">The table's catalog object identity.</param>
    /// <returns>The captured indexes, or an empty list when none exist.</returns>
    public IReadOnlyList<SqlCatalogIndex> GetIndexes(ulong objectId)
        => _indexes.TryGetValue(objectId, out var indexes) ? indexes : Array.Empty<SqlCatalogIndex>();

    /// <summary>Finds a captured table by its case-insensitive SQL namespace and name.</summary>
    /// <param name="schema">The SQL namespace.</param>
    /// <param name="name">The table name.</param>
    /// <param name="table">The captured table when found; otherwise null.</param>
    /// <returns>True when the captured directory contains the table; otherwise false.</returns>
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
