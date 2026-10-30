using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Sql.Internal;

[global::System.Diagnostics.DebuggerDisplay("SqlBoundJsonAccess")]
[global::System.Diagnostics.DebuggerTypeProxy(typeof(SqlBoundJsonAccess.DebugView))]
internal sealed class SqlBoundJsonAccess : SqlBoundExpression
{
    internal SqlBoundJsonAccess(SqlBoundExpression operand, SqlBoundJsonStep[] steps, DatabaseType source, bool asText)
        : base(SqlBoundExpressionKind.JsonAccess)
    {
        Operand = operand;
        Steps = steps;
        Source = source;
        AsText = asText;
    }

    internal SqlBoundExpression Operand { get; }
    internal SqlBoundJsonStep[] Steps { get; }
    internal DatabaseType Source { get; }
    internal bool AsText { get; }

    private sealed class DebugView
    {
        public DebugView(SqlBoundJsonAccess value) { _ = value; }
    }
}
