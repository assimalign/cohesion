namespace Assimalign.Cohesion.Database.Sql.Language;

using Assimalign.Cohesion.Database.Language;

/// <summary>
/// Represents a <c>BETWEEN</c> predicate such as <c>x BETWEEN 1 AND 10</c>.
/// </summary>
public sealed class SqlBetweenExpression : SqlExpression
{
    /// <summary>
    /// Initializes a new <see cref="SqlBetweenExpression"/>.
    /// </summary>
    /// <param name="operand">The expression being tested.</param>
    /// <param name="low">The low bound.</param>
    /// <param name="high">The high bound.</param>
    /// <param name="isNegated">Whether the predicate is negated (<c>NOT BETWEEN</c>).</param>
    /// <param name="location">The source location.</param>
    internal SqlBetweenExpression(SqlExpression operand, SqlExpression low, SqlExpression high, bool isNegated, Location? location)
        : base(location)
    {
        Operand = operand;
        Low = low;
        High = high;
        IsNegated = isNegated;
    }

    /// <summary>
    /// Gets the expression being tested.
    /// </summary>
    public SqlExpression Operand { get; }

    /// <summary>
    /// Gets the low bound of the range.
    /// </summary>
    public SqlExpression Low { get; }

    /// <summary>
    /// Gets the high bound of the range.
    /// </summary>
    public SqlExpression High { get; }

    /// <summary>
    /// Gets whether the predicate is negated (<c>NOT BETWEEN</c>).
    /// </summary>
    public bool IsNegated { get; }
}
