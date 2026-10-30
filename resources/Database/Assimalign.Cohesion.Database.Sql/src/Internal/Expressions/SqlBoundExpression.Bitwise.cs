using Assimalign.Cohesion.Database.Language;
using Assimalign.Cohesion.Database.Sql.Language;
using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Sql.Internal;

/// <summary>
/// An integer bitwise operation with its resolved SQL width. A null right operand denotes NOT.
/// Keeping this metadata on its own leaf preserves ordinary binary and unary allocation sizes.
/// </summary>
[global::System.Diagnostics.DebuggerDisplay("SqlBoundBitwise")]
[global::System.Diagnostics.DebuggerTypeProxy(typeof(SqlBoundBitwise.DebugView))]
internal sealed class SqlBoundBitwise : SqlBoundExpression
{
    internal SqlBoundBitwise(SqlBinaryOperator @operator, SqlBoundExpression left, SqlBoundExpression? right,
        DatabaseType type, Location? location)
        : base(SqlBoundExpressionKind.Bitwise)
    {
        Operator = @operator;
        Left = left;
        Right = right;
        Type = type;
        Location = location;
    }

    internal SqlBinaryOperator Operator { get; }
    internal SqlBoundExpression Left { get; }
    internal SqlBoundExpression? Right { get; }
    internal DatabaseType Type { get; }
    internal Location? Location { get; }

    private sealed class DebugView
    {
        public DebugView(SqlBoundBitwise value) { _ = value; }
    }
}
