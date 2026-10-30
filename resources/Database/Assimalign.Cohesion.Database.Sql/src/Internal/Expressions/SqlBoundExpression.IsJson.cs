using Assimalign.Cohesion.Database.Sql.Language;

namespace Assimalign.Cohesion.Database.Sql.Internal;

[global::System.Diagnostics.DebuggerDisplay("SqlBoundIsJson")]
[global::System.Diagnostics.DebuggerTypeProxy(typeof(SqlBoundIsJson.DebugView))]
internal sealed class SqlBoundIsJson : SqlBoundExpression
{
    internal SqlBoundIsJson(SqlBoundExpression operand, SqlJsonKind kind, bool isNegated)
        : base(SqlBoundExpressionKind.IsJson)
    {
        Operand = operand;
        JsonKind = kind;
        IsNegated = isNegated;
    }

    internal SqlBoundExpression Operand { get; }
    internal SqlJsonKind JsonKind { get; }
    internal bool IsNegated { get; }

    private sealed class DebugView
    {
        public DebugView(SqlBoundIsJson value) { _ = value; }
    }
}
