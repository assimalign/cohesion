namespace Assimalign.Cohesion.Database.Sql.Language;

/// <summary>
/// Represents a single item in an ORDER BY clause.
/// </summary>
public sealed class SqlOrderByColumn
{
    /// <summary>
    /// Initializes a new <see cref="SqlOrderByColumn"/>.
    /// </summary>
    /// <param name="expression">The sort expression.</param>
    /// <param name="isDescending">Whether the sort is descending.</param>
    internal SqlOrderByColumn(SqlExpression expression, bool isDescending)
    {
        Expression = expression;
        IsDescending = isDescending;
    }

    /// <summary>
    /// Gets the sort expression.
    /// </summary>
    public SqlExpression Expression { get; }

    /// <summary>
    /// Gets whether the sort direction is descending.
    /// </summary>
    public bool IsDescending { get; }
}
