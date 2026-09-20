using System;
using System.Collections.Generic;
using System.Linq;

namespace Assimalign.Cohesion.Database.Sql.Catalog;

/// <summary>
/// The catalog's description of one table: identity, columns, and constraints.
/// </summary>
/// <remarks>Collection inputs are copied into read-only collections so published descriptions remain stable.</remarks>
public sealed class SqlCatalogTable
{
    /// <summary>
    /// Initializes a new table description.
    /// </summary>
    /// <param name="objectId">The table's stable object identity.</param>
    /// <param name="schema">The SQL namespace the table belongs to, for example <c>dbo</c>.</param>
    /// <param name="name">The table name, unique within its schema.</param>
    /// <param name="columns">The ordered column definitions.</param>
    /// <param name="primaryKeyColumns">The primary-key column names, or empty when the table has no primary key.</param>
    /// <param name="owner">Whether a compiled schema or an ad-hoc statement created the table.</param>
    /// <param name="owningSchema">The compiled schema that provisioned the table, or null for an ad-hoc table.</param>
    /// <param name="constraints">The foreign-key and check constraints; unique constraints are catalog indexes.</param>
    public SqlCatalogTable(
        ulong objectId,
        string schema,
        string name,
        IReadOnlyList<SqlCatalogColumn> columns,
        IReadOnlyList<string>? primaryKeyColumns = null,
        DatabaseObjectOwner owner = DatabaseObjectOwner.Adhoc,
        string? owningSchema = null,
        IReadOnlyList<SqlCatalogConstraint>? constraints = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(schema);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(columns);
        SqlCatalogOwnership.Validate(owner, owningSchema);

        if (columns.Count == 0)
        {
            throw new SqlCatalogException($"Table '{schema}.{name}' must declare at least one column.");
        }

        ObjectId = objectId;
        Schema = schema;
        Name = name;
        Columns = Array.AsReadOnly(columns.ToArray());
        PrimaryKeyColumns = Array.AsReadOnly(primaryKeyColumns?.ToArray() ?? []);
        Owner = owner;
        OwningSchema = owningSchema;
        var constraintCopy = new SqlCatalogConstraint[constraints?.Count ?? 0];
        for (int index = 0; index < constraintCopy.Length; index++)
        {
            constraintCopy[index] = constraints![index] ?? throw new ArgumentException("A constraint cannot be null.", nameof(constraints));
        }
        Constraints = Array.AsReadOnly(constraintCopy);
    }

    /// <summary>
    /// Gets the table's stable object identity — the value data rows, index
    /// registrations, and lock resources are keyed by.
    /// </summary>
    public ulong ObjectId { get; }

    /// <summary>
    /// Gets the SQL namespace the table belongs to, for example <c>dbo</c>.
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

    /// <summary>
    /// Gets the compiled schema that provisioned this table, or null for an ad-hoc table.
    /// This is distinct from <see cref="Schema"/>, which is the table's SQL namespace.
    /// </summary>
    public string? OwningSchema { get; }

    /// <summary>
    /// Gets the ordered column definitions.
    /// </summary>
    public IReadOnlyList<SqlCatalogColumn> Columns { get; }

    /// <summary>
    /// Gets the primary-key column names (empty when the table has none).
    /// </summary>
    public IReadOnlyList<string> PrimaryKeyColumns { get; }

    /// <summary>Gets the immutable foreign-key and check constraint definitions.</summary>
    public IReadOnlyList<SqlCatalogConstraint> Constraints { get; }

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
