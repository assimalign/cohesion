namespace Assimalign.Cohesion.Database.Sql.Internal;

/// <summary><c>IS [NOT] NULL</c> over a bound operand.</summary>
internal sealed class SqlBoundIsNull : SqlBoundExpression
{
    /// <summary>Initializes a bound null test.</summary>
    /// <param name="operand">The bound operand.</param>
    /// <param name="isNegated"><see langword="true"/> for <c>IS NOT NULL</c>.</param>
    internal SqlBoundIsNull(SqlBoundExpression operand, bool isNegated)
        : base(SqlBoundExpressionKind.IsNull)
    {
        Operand = operand;
        IsNegated = isNegated;
    }

    /// <summary>Gets the bound operand.</summary>
    internal SqlBoundExpression Operand { get; }

    /// <summary>Gets whether the test is <c>IS NOT NULL</c>.</summary>
    internal bool IsNegated { get; }
}
