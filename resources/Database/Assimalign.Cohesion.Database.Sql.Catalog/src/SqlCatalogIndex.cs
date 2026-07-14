using System;
using System.Collections.Generic;

namespace Assimalign.Cohesion.Database.Sql.Catalog;

/// <summary>
/// The catalog's description of one secondary index: name, owning table, ordered
/// key columns, and uniqueness. The physical tree identity (root page id) is not
/// part of this description — it lives in the index-registration record the index
/// manager exports (<see cref="ISqlCatalog.SaveIndexRegistrationsAsync"/>), because
/// root page ids drift on splits while the schema-level description is stable.
/// </summary>
public sealed class SqlCatalogIndex
{
    /// <summary>
    /// Initializes a new index description.
    /// </summary>
    /// <param name="tableObjectId">The object identity of the table the index belongs to.</param>
    /// <param name="name">The index name, unique within its table.</param>
    /// <param name="columnNames">The ordered key column names.</param>
    /// <param name="isUnique">Whether the index enforces key uniqueness.</param>
    public SqlCatalogIndex(ulong tableObjectId, string name, IReadOnlyList<string> columnNames, bool isUnique)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(columnNames);

        if (columnNames.Count == 0)
        {
            throw new SqlCatalogException($"Index '{name}' must declare at least one key column.");
        }

        TableObjectId = tableObjectId;
        Name = name;
        ColumnNames = columnNames;
        IsUnique = isUnique;
    }

    /// <summary>
    /// Gets the object identity of the table the index belongs to.
    /// </summary>
    public ulong TableObjectId { get; }

    /// <summary>
    /// Gets the index name, unique within its table.
    /// </summary>
    public string Name { get; }

    /// <summary>
    /// Gets the ordered key column names.
    /// </summary>
    public IReadOnlyList<string> ColumnNames { get; }

    /// <summary>
    /// Gets a value indicating whether the index enforces key uniqueness.
    /// </summary>
    public bool IsUnique { get; }
}
