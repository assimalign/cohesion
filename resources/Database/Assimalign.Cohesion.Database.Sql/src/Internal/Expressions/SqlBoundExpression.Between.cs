namespace Assimalign.Cohesion.Database.Sql.Internal;

/// <summary>
/// <c>[NOT] BETWEEN</c> over bound operands, with the collation of the operand and each bound
/// resolved when the expression was bound.
/// </summary>
internal sealed class SqlBoundBetween : SqlBoundExpression
{
    /// <summary>Initializes a bound range test.</summary>
    /// <param name="operand">The bound tested value.</param>
    /// <param name="low">The bound lower end.</param>
    /// <param name="high">The bound upper end.</param>
    /// <param name="isNegated"><see langword="true"/> for <c>NOT BETWEEN</c>.</param>
    /// <param name="lowCollation">The collation of the operand and the lower end.</param>
    /// <param name="highCollation">The collation of the operand and the upper end.</param>
    internal SqlBoundBetween(SqlBoundExpression operand, SqlBoundExpression low, SqlBoundExpression high, bool isNegated,
        SqlBoundCollation lowCollation, SqlBoundCollation highCollation)
        : base(SqlBoundExpressionKind.Between)
    {
        Operand = operand;
        Low = low;
        High = high;
        IsNegated = isNegated;
        LowCollation = lowCollation;
        HighCollation = highCollation;
    }

    /// <summary>Gets the bound tested value.</summary>
    internal SqlBoundExpression Operand { get; }

    /// <summary>Gets the bound lower end.</summary>
    internal SqlBoundExpression Low { get; }

    /// <summary>Gets the bound upper end.</summary>
    internal SqlBoundExpression High { get; }

    /// <summary>Gets whether the test is <c>NOT BETWEEN</c>.</summary>
    internal bool IsNegated { get; }

    /// <summary>Gets the collation of the operand and the lower end.</summary>
    internal SqlBoundCollation LowCollation { get; }

    /// <summary>Gets the collation of the operand and the upper end.</summary>
    internal SqlBoundCollation HighCollation { get; }
}
