using System.Collections.Generic;

using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Sql.Catalog;

/// <summary>
/// A consistent, read-only capture of a SQL catalog's table and index descriptions.
/// </summary>
/// <remarks>
/// All descriptions and the default collation are captured together. Later catalog
/// publications do not change this capture; it owns no storage or disposal lifetime.
/// </remarks>
public interface ISqlCatalogSnapshot
{
    /// <summary>Gets the table descriptions present at capture time.</summary>
    IReadOnlyList<SqlCatalogTable> Tables { get; }

    /// <summary>Gets the database default collation at capture time.</summary>
    Collation DefaultCollation { get; }

    /// <summary>Gets the captured index descriptions for a table.</summary>
    /// <param name="objectId">The table's catalog object identity.</param>
    /// <returns>The captured indexes, or an empty list when none exist.</returns>
    IReadOnlyList<SqlCatalogIndex> GetIndexes(ulong objectId);

    /// <summary>Finds a captured table by its case-insensitive SQL namespace and name.</summary>
    /// <param name="schema">The SQL namespace.</param>
    /// <param name="name">The table name.</param>
    /// <param name="table">The captured table when found; otherwise null.</param>
    /// <returns>True when the captured directory contains the table; otherwise false.</returns>
    bool TryGetTable(string schema, string name, out SqlCatalogTable table);
}
