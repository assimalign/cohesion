using System;
using System.Collections.Generic;
using System.Linq;

using Assimalign.Cohesion.Database.Sql.Catalog.Internal;

namespace Assimalign.Cohesion.Database.Sql.Catalog;

/// <summary>
/// The catalog's description of one table: identity, columns, constraints, and the
/// physical layout its stored rows use.
/// </summary>
/// <remarks>
/// <para>
/// Collection inputs are copied into read-only collections so published descriptions remain stable.
/// </para>
/// <para>
/// <see cref="Columns"/> are the table's live columns, in order: what <c>SELECT *</c>
/// returns, what INSERT without a column list fills and what every name resolves
/// against. A row version stores one component per <i>physical ordinal</i>, and a
/// column keeps its physical ordinal for the life of the table. Dropping a column
/// removes it from <see cref="Columns"/> and records its physical ordinal in
/// <see cref="DroppedColumnOrdinals"/>; adding one appends a new physical ordinal
/// after every existing one. A physical ordinal is never reused, so a component
/// always belongs to the same column, whichever definition wrote or reads the
/// version. This is PostgreSQL's dropped-attribute design: a dropped column keeps its
/// <c>attnum</c> with <c>attisdropped</c> set (<c>src/include/catalog/pg_attribute.h:139-140</c>),
/// and a new column takes <c>relnatts + 1</c> (<c>src/backend/commands/tablecmds.c:7445-7446</c>).
/// </para>
/// </remarks>
public sealed class SqlCatalogTable
{
    private readonly int[] _physicalOrdinals;

    /// <summary>
    /// Initializes a new table description.
    /// </summary>
    /// <param name="objectId">The table's stable object identity.</param>
    /// <param name="schema">The SQL namespace the table belongs to, for example <c>dbo</c>.</param>
    /// <param name="name">The table name, unique within its schema.</param>
    /// <param name="columns">The ordered live column definitions.</param>
    /// <param name="primaryKeyColumns">The primary-key column names, or empty when the table has no primary key.</param>
    /// <param name="owner">Whether a compiled schema or an ad-hoc statement created the table.</param>
    /// <param name="owningSchema">The compiled schema that provisioned the table, or null for an ad-hoc table.</param>
    /// <param name="constraints">The foreign-key and check constraints; unique constraints are catalog indexes.</param>
    /// <param name="droppedColumnOrdinals">
    /// The physical ordinals of the table's dropped columns, in any order, or null when no
    /// column was ever dropped. The live columns take the remaining physical ordinals in order.
    /// </param>
    /// <exception cref="ArgumentException">
    /// A dropped ordinal is negative, repeated, or not below the physical column count
    /// (the live columns plus the dropped ones).
    /// </exception>
    public SqlCatalogTable(
        ulong objectId,
        string schema,
        string name,
        IReadOnlyList<SqlCatalogColumn> columns,
        IReadOnlyList<string>? primaryKeyColumns = null,
        DatabaseObjectOwner owner = DatabaseObjectOwner.Adhoc,
        string? owningSchema = null,
        IReadOnlyList<SqlCatalogConstraint>? constraints = null,
        IReadOnlyList<int>? droppedColumnOrdinals = null)
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

        int[] dropped = droppedColumnOrdinals?.ToArray() ?? [];
        Array.Sort(dropped);
        PhysicalColumnCount = columns.Count + dropped.Length;
        for (int index = 0; index < dropped.Length; index++)
        {
            if (dropped[index] < 0 || dropped[index] >= PhysicalColumnCount)
            {
                throw new ArgumentException(
                    $"Dropped column ordinal {dropped[index]} of '{schema}.{name}' is outside its {PhysicalColumnCount} physical columns.",
                    nameof(droppedColumnOrdinals));
            }

            if (index > 0 && dropped[index] == dropped[index - 1])
            {
                throw new ArgumentException(
                    $"Dropped column ordinal {dropped[index]} of '{schema}.{name}' is listed more than once.", nameof(droppedColumnOrdinals));
            }
        }

        DroppedColumnOrdinals = Array.AsReadOnly(dropped);
        _physicalOrdinals = new int[columns.Count];
        for (int physical = 0, live = 0, next = 0; physical < PhysicalColumnCount; physical++)
        {
            if (next < dropped.Length && dropped[next] == physical)
            {
                next++;
                continue;
            }

            _physicalOrdinals[live++] = physical;
        }
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
    /// Gets the ordered live column definitions. A dropped column is not among them.
    /// </summary>
    public IReadOnlyList<SqlCatalogColumn> Columns { get; }

    /// <summary>
    /// Gets the number of physical columns: the components a row version written under
    /// this definition stores, one per live column and one per dropped column. A version
    /// written under an earlier definition can store fewer (the columns added since are its
    /// missing tail) and one written under a later definition can store more.
    /// </summary>
    public int PhysicalColumnCount { get; }

    /// <summary>
    /// Gets the physical ordinals of the dropped columns, ascending. A version stores a
    /// component at each of them (the dropped value, or NULL when written after the drop)
    /// that every read skips; the ordinals are never reused.
    /// </summary>
    public IReadOnlyList<int> DroppedColumnOrdinals { get; }

    /// <summary>
    /// Gets the primary-key column names (empty when the table has none).
    /// </summary>
    public IReadOnlyList<string> PrimaryKeyColumns { get; }

    /// <summary>Gets the immutable foreign-key and check constraint definitions.</summary>
    public IReadOnlyList<SqlCatalogConstraint> Constraints { get; }

    /// <summary>
    /// Gets the physical ordinal of a live column: the position of its component in every
    /// row version stored for the table.
    /// </summary>
    /// <param name="ordinal">The column's position in <see cref="Columns"/>.</param>
    /// <returns>The column's physical ordinal, at least <paramref name="ordinal"/>.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="ordinal"/> is not a position in <see cref="Columns"/>.</exception>
    public int GetPhysicalOrdinal(int ordinal)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(ordinal);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(ordinal, _physicalOrdinals.Length);
        return _physicalOrdinals[ordinal];
    }

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
