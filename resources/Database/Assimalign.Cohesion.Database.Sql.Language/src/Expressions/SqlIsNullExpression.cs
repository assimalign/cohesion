namespace Assimalign.Cohesion.Database.Sql.Language;

using Assimalign.Cohesion.Database.Language;

/// <summary>
/// Represents an <c>IS NULL</c> or <c>IS NOT NULL</c> predicate.
/// </summary>
public sealed class SqlIsNullExpression : SqlExpression
{
    /// <summary>
    /// Initializes a new <see cref="SqlIsNullExpression"/>.
    /// </summary>
    /// <param name="operand">The expression being tested.</param>
    /// <param name="isNegated">Whether the predicate is negated (<c>IS NOT NULL</c>).</param>
    /// <param name="location">The source location.</param>
    internal SqlIsNullExpression(SqlExpression operand, bool isNegated, Location? location)
        : base(location)
    {
        Operand = operand;
        IsNegated = isNegated;
    }

    /// <summary>
    /// Gets the expression being tested.
    /// </summary>
    public SqlExpression Operand { get; }

    /// <summary>
    /// Gets whether the predicate is negated (<c>IS NOT NULL</c>).
    /// </summary>
    public bool IsNegated { get; }
}
