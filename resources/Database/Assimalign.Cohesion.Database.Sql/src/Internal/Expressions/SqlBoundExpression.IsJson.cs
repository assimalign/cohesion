using Assimalign.Cohesion.Database.Sql.Language;
using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Sql.Internal;

[global::System.Diagnostics.DebuggerDisplay("SqlBoundIsJson")]
[global::System.Diagnostics.DebuggerTypeProxy(typeof(SqlBoundIsJson.DebugView))]
internal sealed class SqlBoundIsJson : SqlBoundExpression
{
    internal SqlBoundIsJson(SqlBoundExpression operand, DatabaseType source, SqlJsonKind kind, bool isNegated)
        : base(SqlBoundExpressionKind.IsJson)
    {
        Operand = operand;
        Source = source;
        JsonKind = kind;
        IsNegated = isNegated;
    }

    internal SqlBoundExpression Operand { get; }
    internal DatabaseType Source { get; }
    internal SqlJsonKind JsonKind { get; }
    internal bool IsNegated { get; }

    private sealed class DebugView
    {
        public DebugView(SqlBoundIsJson value) { _ = value; }
    }
}
