using Assimalign.Cohesion.Database.Sql.Language;

namespace Assimalign.Cohesion.Database.Sql.Internal;

/// <summary>
/// A unary operator over a bound operand: negation, ISO unary plus, or <c>NOT</c>. A negated integer
/// literal that spells the BIGINT minimum binds as a <see cref="SqlBoundConstant"/> instead, because
/// its magnitude alone does not fit BIGINT.
/// </summary>
internal sealed class SqlBoundUnary : SqlBoundExpression
{
    /// <summary>Initializes a bound unary operator.</summary>
    /// <param name="operator">The operator.</param>
    /// <param name="operand">The bound operand.</param>
    internal SqlBoundUnary(SqlUnaryOperator @operator, SqlBoundExpression operand)
        : base(SqlBoundExpressionKind.Unary)
    {
        Operator = @operator;
        Operand = operand;
    }

    /// <summary>Gets the operator.</summary>
    internal SqlUnaryOperator Operator { get; }

    /// <summary>Gets the bound operand.</summary>
    internal SqlBoundExpression Operand { get; }
}
