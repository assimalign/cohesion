namespace Assimalign.Cohesion.Database.Sql.Internal;

/// <summary>
/// <c>[NOT] LIKE</c> over a bound value and pattern, with their collation resolved when the
/// expression was bound.
/// </summary>
internal sealed class SqlBoundLike : SqlBoundExpression
{
    /// <summary>Initializes a bound pattern match.</summary>
    /// <param name="operand">The bound matched value.</param>
    /// <param name="pattern">The bound pattern.</param>
    /// <param name="isNegated"><see langword="true"/> for <c>NOT LIKE</c>.</param>
    /// <param name="collation">The collation of the value and the pattern.</param>
    internal SqlBoundLike(SqlBoundExpression operand, SqlBoundExpression pattern, bool isNegated, SqlBoundCollation collation)
        : base(SqlBoundExpressionKind.Like)
    {
        Operand = operand;
        Pattern = pattern;
        IsNegated = isNegated;
        Collation = collation;
    }

    /// <summary>Gets the bound matched value.</summary>
    internal SqlBoundExpression Operand { get; }

    /// <summary>Gets the bound pattern.</summary>
    internal SqlBoundExpression Pattern { get; }

    /// <summary>Gets whether the match is <c>NOT LIKE</c>.</summary>
    internal bool IsNegated { get; }

    /// <summary>Gets the collation of the value and the pattern.</summary>
    internal SqlBoundCollation Collation { get; }
}
