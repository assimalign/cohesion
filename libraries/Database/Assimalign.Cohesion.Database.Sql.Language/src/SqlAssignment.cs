namespace Assimalign.Cohesion.Database.Language.Sql;

/// <summary>
/// Represents a column assignment in an UPDATE SET clause, such as <c>Name = 'Bob'</c>.
/// </summary>
public sealed class SqlAssignment
{
    /// <summary>
    /// Initializes a new <see cref="SqlAssignment"/>.
    /// </summary>
    /// <param name="columnName">The column being assigned.</param>
    /// <param name="value">The value expression.</param>
    internal SqlAssignment(string columnName, SqlExpression value)
    {
        ColumnName = columnName;
        Value = value;
    }

    /// <summary>
    /// Gets the name of the column being assigned.
    /// </summary>
    public string ColumnName { get; }

    /// <summary>
    /// Gets the value expression being assigned to the column.
    /// </summary>
    public SqlExpression Value { get; }
}
