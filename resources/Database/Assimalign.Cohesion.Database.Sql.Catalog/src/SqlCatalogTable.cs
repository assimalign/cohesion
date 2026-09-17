using System;
using System.Collections.Generic;

namespace Assimalign.Cohesion.Database.Sql.Catalog;

/// <summary>
/// The catalog's description of one table: identity, columns, and constraints.
/// </summary>
public sealed class SqlCatalogTable
{
    /// <summary>
    /// Initializes a new table description.
    /// </summary>
    /// <param name="objectId">The table's stable object identity.</param>
    /// <param name="schema">The schema the table belongs to.</param>
    /// <param name="name">The table name, unique within its schema.</param>
    /// <param name="columns">The ordered column definitions.</param>
    /// <param name="primaryKeyColumns">The primary-key column names, or empty when the table has no primary key.</param>
    /// <param name="owner">Whether a compiled schema or an ad-hoc statement created the table.</param>
    /// <param name="schemaName">The compiled schema that owns the table, or null for ad-hoc tables.</param>
    public SqlCatalogTable(
        ulong objectId,
        string schema,
        string name,
        IReadOnlyList<SqlCatalogColumn> columns,
        IReadOnlyList<string>? primaryKeyColumns = null,
        DatabaseObjectOwner owner = DatabaseObjectOwner.Adhoc,
        string? schemaName = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(schema);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(columns);
        SqlCatalogOwnership.Validate(owner, schemaName);

        if (columns.Count == 0)
        {
            throw new SqlCatalogException($"Table '{schema}.{name}' must declare at least one column.");
        }

        ObjectId = objectId;
        Schema = schema;
        Name = name;
        Columns = columns;
        PrimaryKeyColumns = primaryKeyColumns ?? Array.Empty<string>();
        Owner = owner;
        SchemaName = schemaName;
    }

    /// <summary>
    /// Gets the table's stable object identity — the value data rows, index
    /// registrations, and lock resources are keyed by.
    /// </summary>
    public ulong ObjectId { get; }

    /// <summary>
    /// Gets the schema the table belongs to.
    /// </summary>
    public string Schema { get; }

    /// <summary>
    /// Gets the table name, unique within its schema.
    /// </summary>
    public string Name { get; }

    /// <summary>
    /// Gets what created this table. Code-first schema tables can only be changed by
    /// schema application; tables created by ad-hoc statements remain mutable by those statements.
    /// </summary>
    public DatabaseObjectOwner Owner { get; }

    /// <summary>Gets the compiled schema that owns this table, or null for an ad-hoc table.</summary>
    public string? SchemaName { get; }

    /// <summary>
    /// Gets the ordered column definitions.
    /// </summary>
    public IReadOnlyList<SqlCatalogColumn> Columns { get; }

    /// <summary>
    /// Gets the primary-key column names (empty when the table has none).
    /// </summary>
    public IReadOnlyList<string> PrimaryKeyColumns { get; }

    /// <summary>
    /// Finds a column by name (ordinal, case-insensitive per SQL identifier rules).
    /// </summary>
    /// <param name="name">The column name.</param>
    /// <returns>The column, or null when no column has the name.</returns>
    public SqlCatalogColumn? FindColumn(string name)
    {
        foreach (var column in Columns)
        {
            if (string.Equals(column.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                return column;
            }
        }

        return null;
    }
}
