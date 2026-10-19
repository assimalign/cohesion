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
    /// <param name="nullPlacement">The explicit NULL placement, or the dialect default.</param>
    internal SqlOrderByColumn(SqlExpression expression, bool isDescending, SqlNullPlacement nullPlacement)
    {
        Expression = expression;
        IsDescending = isDescending;
        NullPlacement = nullPlacement;
    }

    /// <summary>
    /// Gets the sort expression.
    /// </summary>
    public SqlExpression Expression { get; }

    /// <summary>
    /// Gets whether the sort direction is descending.
    /// </summary>
    public bool IsDescending { get; }

    /// <summary>
    /// Gets the written NULL placement, or <see cref="SqlNullPlacement.Default"/> when omitted.
    /// </summary>
    public SqlNullPlacement NullPlacement { get; }
}
