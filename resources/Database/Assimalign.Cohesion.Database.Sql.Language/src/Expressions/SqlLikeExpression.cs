namespace Assimalign.Cohesion.Database.Sql.Language;

using Assimalign.Cohesion.Database.Language;

/// <summary>
/// Represents a <c>LIKE</c> predicate such as <c>name LIKE '%test%'</c>.
/// </summary>
public sealed class SqlLikeExpression : SqlExpression
{
    /// <summary>
    /// Initializes a new <see cref="SqlLikeExpression"/>.
    /// </summary>
    /// <param name="operand">The expression being tested.</param>
    /// <param name="pattern">The pattern expression.</param>
    /// <param name="isNegated">Whether the predicate is negated (<c>NOT LIKE</c>).</param>
    /// <param name="location">The source location.</param>
    internal SqlLikeExpression(SqlExpression operand, SqlExpression pattern, bool isNegated, Location? location)
        : base(location)
    {
        Operand = operand;
        Pattern = pattern;
        IsNegated = isNegated;
    }

    /// <summary>
    /// Gets the expression being tested.
    /// </summary>
    public SqlExpression Operand { get; }

    /// <summary>
    /// Gets the pattern expression.
    /// </summary>
    public SqlExpression Pattern { get; }

    /// <summary>
    /// Gets whether the predicate is negated (<c>NOT LIKE</c>).
    /// </summary>
    public bool IsNegated { get; }
}
