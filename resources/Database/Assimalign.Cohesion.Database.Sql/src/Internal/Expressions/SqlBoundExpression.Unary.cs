using Assimalign.Cohesion.Database.Language;
using Assimalign.Cohesion.Database.Sql.Language;

namespace Assimalign.Cohesion.Database.Sql.Internal;

/// <summary>
/// A unary operator over a bound operand: negation, ISO unary plus, or <c>NOT</c>. A negated integer
/// literal that spells the BIGINT minimum binds as a <see cref="SqlBoundConstant"/> instead, because
/// its magnitude alone does not fit BIGINT.
/// </summary>
[global::System.Diagnostics.DebuggerDisplay("SqlBoundUnary")]
[global::System.Diagnostics.DebuggerTypeProxy(typeof(global::Assimalign.Cohesion.Database.Sql.Internal.SqlBoundUnary.DebugView))]
internal sealed class SqlBoundUnary : SqlBoundExpression
{
    /// <summary>Initializes a bound unary operator.</summary>
    /// <param name="operator">The operator.</param>
    /// <param name="operand">The bound operand.</param>
    /// <param name="location">The source expression span, if available.</param>
    internal SqlBoundUnary(SqlUnaryOperator @operator, SqlBoundExpression operand, Location? location = null)
        : base(SqlBoundExpressionKind.Unary)
    {
        Location = location;
        Operator = @operator;
        Operand = operand;
    }

    /// <summary>Gets the operator.</summary>
    internal SqlUnaryOperator Operator { get; }

    /// <summary>Gets the bound operand.</summary>
    internal SqlBoundExpression Operand { get; }

    /// <summary>Gets the source expression span.</summary>
    internal Location? Location { get; }

    private sealed class DebugView
    {
        public DebugView(global::Assimalign.Cohesion.Database.Sql.Internal.SqlBoundUnary value)
        {
            _ = value;
        }
    }
}
