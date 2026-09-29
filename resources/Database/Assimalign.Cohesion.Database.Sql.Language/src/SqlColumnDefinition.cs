using System.Collections.Generic;

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
    internal SqlColumnDefinition(string columnName, string dataType, bool isNullable, bool isPrimaryKey, SqlExpression? defaultValue,
        IReadOnlyList<SqlConstraintDefinition>? constraints = null, string? collationName = null)
    {
        ColumnName = columnName;
        DataType = dataType;
        IsNullable = isNullable;
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
    /// Gets whether the column allows NULL values.
    /// </summary>
    public bool IsNullable { get; }

    /// <summary>
    /// Gets whether the column is marked as a primary key.
    /// </summary>
    public bool IsPrimaryKey { get; }

    /// <summary>
    /// Gets the default value expression, if present.
    /// </summary>
    public SqlExpression? DefaultValue { get; }

    /// <summary>Gets normalized constraints declared on this column.</summary>
    public IReadOnlyList<SqlConstraintDefinition> Constraints { get; }

    /// <summary>Gets the declared collation name, or null to inherit the database default.</summary>
    public string? CollationName { get; }
}
