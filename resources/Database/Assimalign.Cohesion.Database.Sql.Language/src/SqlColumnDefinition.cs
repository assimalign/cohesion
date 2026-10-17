using System.Collections.Generic;

using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Sql.Language;

/// <summary>
/// Represents a column definition in a CREATE TABLE statement.
/// </summary>
public sealed class SqlColumnDefinition
{
    /// <summary>
    /// Initializes a new <see cref="SqlColumnDefinition"/>.
    /// </summary>
    /// <param name="columnName">The column name.</param>
    /// <param name="dataType">The data type name.</param>
    /// <param name="isNullable">Whether the column allows NULL values.</param>
    /// <param name="isPrimaryKey">Whether the column is a primary key.</param>
    /// <param name="defaultValue">The default value expression, if any.</param>
    /// <param name="constraints">The normalized column constraints.</param>
    /// <param name="collationName">The declared collation name, or null to inherit the database default.</param>
    /// <param name="identity">The identity generation facet, when declared.</param>
    /// <param name="resolvedType">The resolved built-in type, or null for an engine-resolved domain.</param>
    /// <param name="isExplicitlyNullable">Whether a NULL declaration was written, for table-constraint conflict checking.</param>
    internal SqlColumnDefinition(string columnName, string dataType, bool isNullable, bool isPrimaryKey, SqlExpression? defaultValue,
        IReadOnlyList<SqlConstraintDefinition>? constraints = null, string? collationName = null, DatabaseTypeInfo? resolvedType = null, SqlIdentityDefinition? identity = null,
        bool isExplicitlyNullable = false)
    {
        Identity = identity;
        ColumnName = columnName;
        DataType = dataType;
        ResolvedType = resolvedType;
        IsNullable = isNullable;
        IsExplicitlyNullable = isExplicitlyNullable;
        IsPrimaryKey = isPrimaryKey;
        DefaultValue = defaultValue;
        Constraints = constraints ?? [];
        CollationName = collationName;
    }

    /// <summary>
    /// Gets the column name.
    /// </summary>
    public string ColumnName { get; }

    /// <summary>
    /// Gets the data type name (e.g., <c>INT</c>, <c>TEXT</c>, <c>VARCHAR(100)</c>).
    /// </summary>
    public string DataType { get; }

    /// <summary>
    /// Gets the built-in type resolved during parsing, or null when the name requires
    /// the engine's domain catalog or the type is invalid.
    /// </summary>
    public DatabaseTypeInfo? ResolvedType { get; }

    /// <summary>
    /// Gets whether the column allows NULL values.
    /// </summary>
    public bool IsNullable { get; }

    internal bool IsExplicitlyNullable { get; }

    /// <summary>
    /// Gets whether the column is marked as a primary key.
    /// </summary>
    public bool IsPrimaryKey { get; }

    /// <summary>
    /// Gets the default value expression, if present.
    /// </summary>
    public SqlExpression? DefaultValue { get; }

    /// <summary>Gets the identity facet, or null for an ordinary column.</summary>
    public SqlIdentityDefinition? Identity { get; }

    /// <summary>Gets normalized constraints declared on this column.</summary>
    public IReadOnlyList<SqlConstraintDefinition> Constraints { get; }

    /// <summary>Gets the declared collation name, or null to inherit the database default.</summary>
    public string? CollationName { get; }
}
