using Assimalign.Cohesion.Database.Language;
using Assimalign.Cohesion.Database.Sql.Language;

namespace Assimalign.Cohesion.Database.Sql.Internal;

/// <summary>
/// A binary operator over two bound operands, with the collation a comparison of them uses resolved
/// when the expression was bound rather than searched for in both operand trees on every row.
/// </summary>
[global::System.Diagnostics.DebuggerDisplay("SqlBoundBinary")]
[global::System.Diagnostics.DebuggerTypeProxy(typeof(global::Assimalign.Cohesion.Database.Sql.Internal.SqlBoundBinary.DebugView))]
internal sealed class SqlBoundBinary : SqlBoundExpression
{
    /// <summary>Initializes a bound binary operator.</summary>
    /// <param name="operator">The operator.</param>
    /// <param name="left">The bound left operand.</param>
    /// <param name="right">The bound right operand.</param>
    /// <param name="collation">The collation of the two operands, as a comparison of them resolves it.</param>
    /// <param name="location">The source expression span, if available.</param>
    internal SqlBoundBinary(SqlBinaryOperator @operator, SqlBoundExpression left, SqlBoundExpression right, SqlBoundCollation collation, Location? location = null)
        : base(SqlBoundExpressionKind.Binary)
    {
        Location = location;
        Operator = @operator;
        Left = left;
        Right = right;
        Collation = collation;
    }

    /// <summary>Gets the operator.</summary>
    internal SqlBinaryOperator Operator { get; }

    /// <summary>Gets the bound left operand.</summary>
    internal SqlBoundExpression Left { get; }

    /// <summary>Gets the bound right operand.</summary>
    internal SqlBoundExpression Right { get; }

    /// <summary>Gets the collation of the two operands.</summary>
    internal SqlBoundCollation Collation { get; }

    /// <summary>Gets the source expression span.</summary>
    internal Location? Location { get; }

    private sealed class DebugView
    {
        public DebugView(global::Assimalign.Cohesion.Database.Sql.Internal.SqlBoundBinary value)
        {
            _ = value;
        }
    }
}
