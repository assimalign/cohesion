using System;
using System.Collections.Generic;
using System.Linq;

using Assimalign.Cohesion.Database.Sql.Internal;

namespace Assimalign.Cohesion.Database.Sql.Catalog;

/// <summary>
/// The catalog's description of one secondary index: name, owning table, ordered
/// key columns, uniqueness and optional row predicate. The physical tree identity (root page id) is not
/// part of this description — it lives in the index-registration record the index
/// manager exports (<see cref="SqlCatalog.CreateIndexAsync"/>), because
/// the description is schema while the registration is physical topology that the
/// index manager owns (its root page stays fixed through splits since #1159).
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
    /// <param name="predicate">Canonical predicate text, or null for an unconditional index.</param>
    /// <param name="predicateColumns">The columns referenced by the predicate.</param>
    /// <param name="predicateFunctionCalls">Resolved function identities in predicate preorder.</param>
    /// <param name="keyOrders">Effective per-column orders, or null for ASC NULLS LAST on every column.</param>
    /// <param name="isPrimaryKey">Whether this index is the physical enforcement of the table's primary key.</param>
    /// <exception cref="SqlCatalogException">Keys are empty, key orders are invalid, a primary key is partial or directed, or predicate metadata is inconsistent.</exception>
    public SqlCatalogIndex(
        ulong tableObjectId,
        string name,
        IReadOnlyList<string> columnNames,
        bool isUnique,
        DatabaseObjectOwner owner = DatabaseObjectOwner.Adhoc,
        string? owningSchema = null,
        bool isPrimaryKey = false,
        string? predicate = null,
        IReadOnlyList<string>? predicateColumns = null,
        IReadOnlyList<string>? predicateFunctionCalls = null,
        IReadOnlyList<SqlCatalogIndexKeyOrder>? keyOrders = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(columnNames);
        SqlCatalogOwnership.Validate(owner, owningSchema);

        if (columnNames.Count == 0)
        {
            throw new SqlCatalogException("CS03059", string.Format(System.Globalization.CultureInfo.CurrentCulture, ErrorMessages.SqlCatalogIndexSqlCatalog, name));
        }

        TableObjectId = tableObjectId;
        Name = name;
        ColumnNames = Array.AsReadOnly(columnNames.ToArray());
        IsUnique = isUnique;
        Owner = owner;
        OwningSchema = owningSchema;
        if (isPrimaryKey && !isUnique)
        {
            throw new ArgumentException(ErrorMessages.SqlCatalogIndexArgument, nameof(isPrimaryKey));
        }
        if (isPrimaryKey && predicate is not null)
        {
            throw new SqlCatalogException("CS03082", ErrorMessages.PartialPrimaryKey);
        }
        if (predicate is null && ((predicateColumns?.Count ?? 0) != 0 || (predicateFunctionCalls?.Count ?? 0) != 0) ||
            predicate is not null && string.IsNullOrWhiteSpace(predicate))
        { throw new SqlCatalogException("CS03084", ErrorMessages.PartialPredicateMetadataInvalid); }
        if (keyOrders is not null && (keyOrders.Count != columnNames.Count ||
            keyOrders.Any(order => (uint)order > 3 || isPrimaryKey && order != SqlCatalogIndexKeyOrder.AscendingNullsLast)))
        { throw new SqlCatalogException("CS03085", ErrorMessages.IndexKeyOrderInvalid); }
        KeyOrders = Array.AsReadOnly(keyOrders?.ToArray() ?? new SqlCatalogIndexKeyOrder[columnNames.Count]);
        IsPrimaryKey = isPrimaryKey;
        Predicate = predicate;
        PredicateColumns = predicateColumns is null ? Array.Empty<string>() : Array.AsReadOnly(predicateColumns.ToArray());
        PredicateFunctionCalls = predicateFunctionCalls is null ? Array.Empty<string>() : Array.AsReadOnly(predicateFunctionCalls.ToArray());
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

    /// <summary>Gets the effective order of each key column, in column order.</summary>
    public IReadOnlyList<SqlCatalogIndexKeyOrder> KeyOrders { get; }

    /// <summary>
    /// Gets a value indicating whether the index enforces key uniqueness.
    /// </summary>
    public bool IsUnique { get; }

    /// <summary>Gets whether this index enforces the table's primary key rather than a separately declared unique constraint.</summary>
    public bool IsPrimaryKey { get; }

    /// <summary>Gets canonical predicate text, or null for an unconditional index.</summary>
    public string? Predicate { get; }

    /// <summary>Gets the columns read by the predicate.</summary>
    public IReadOnlyList<string> PredicateColumns { get; }

    /// <summary>Gets the predicate calls' resolved identities in preorder.</summary>
    public IReadOnlyList<string> PredicateFunctionCalls { get; }

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
