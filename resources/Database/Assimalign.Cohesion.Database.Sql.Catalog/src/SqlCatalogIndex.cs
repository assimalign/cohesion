using System;
using System.Collections.Generic;
using System.Linq;

using Assimalign.Cohesion.Database.Sql.Catalog.Internal;

namespace Assimalign.Cohesion.Database.Sql.Catalog;

/// <summary>
/// The catalog's description of one secondary index: name, owning table, ordered
/// key columns, and uniqueness. The physical tree identity (root page id) is not
/// part of this description — it lives in the index-registration record the index
/// manager exports (<see cref="ISqlCatalog.SaveIndexRegistrationsAsync"/>), because
/// root page ids drift on splits while the schema-level description is stable.
/// </summary>
/// <remarks>Key-column names are copied into a read-only collection so published descriptions remain stable.</remarks>
public sealed class SqlCatalogIndex
{
    /// <summary>
    /// Initializes a new index description.
    /// </summary>
    /// <param name="tableObjectId">The object identity of the table the index belongs to.</param>
    /// <param name="name">The index name, unique within its table.</param>
    /// <param name="columnNames">The ordered key column names.</param>
    /// <param name="isUnique">Whether the index enforces key uniqueness.</param>
    /// <param name="owner">Whether a compiled schema or an ad-hoc statement created the index.</param>
    /// <param name="owningSchema">The compiled schema that provisioned the index, or null for an ad-hoc index.</param>
    /// <param name="isPrimaryKey">Whether this index is the physical enforcement of the table's primary key.</param>
    public SqlCatalogIndex(
        ulong tableObjectId,
        string name,
        IReadOnlyList<string> columnNames,
        bool isUnique,
        DatabaseObjectOwner owner = DatabaseObjectOwner.Adhoc,
        string? owningSchema = null,
        bool isPrimaryKey = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(columnNames);
        SqlCatalogOwnership.Validate(owner, owningSchema);

        if (columnNames.Count == 0)
        {
            throw new SqlCatalogException($"Index '{name}' must declare at least one key column.");
        }

        TableObjectId = tableObjectId;
        Name = name;
        ColumnNames = Array.AsReadOnly(columnNames.ToArray());
        IsUnique = isUnique;
        Owner = owner;
        OwningSchema = owningSchema;
        if (isPrimaryKey && !isUnique)
        {
            throw new ArgumentException("A primary-key index must enforce uniqueness.", nameof(isPrimaryKey));
        }
        IsPrimaryKey = isPrimaryKey;
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

    /// <summary>Gets whether this index enforces the table's primary key rather than a separately declared unique constraint.</summary>
    public bool IsPrimaryKey { get; }

    /// <summary>
    /// Gets what created this index. Code-first schema indexes can only be changed by
    /// schema application; indexes created by ad-hoc statements remain mutable by those statements.
    /// </summary>
    public DatabaseObjectOwner Owner { get; }

    /// <summary>
    /// Gets the compiled schema that provisioned this index, or null for an ad-hoc index.
    /// This is ownership metadata, not the SQL namespace of the index's table.
    /// </summary>
    public string? OwningSchema { get; }
}
