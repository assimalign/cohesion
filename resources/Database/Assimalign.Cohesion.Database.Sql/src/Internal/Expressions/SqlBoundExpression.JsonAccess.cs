using Assimalign.Cohesion.Database.Language;
using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Sql.Internal;

[global::System.Diagnostics.DebuggerDisplay("SqlBoundJsonAccess")]
[global::System.Diagnostics.DebuggerTypeProxy(typeof(SqlBoundJsonAccess.DebugView))]
internal sealed class SqlBoundJsonAccess : SqlBoundExpression
{
    internal SqlBoundJsonAccess(SqlBoundExpression operand, SqlBoundExpression selector, DatabaseType source, bool asText, Location? location)
        : base(SqlBoundExpressionKind.JsonAccess)
    {
        Operand = operand;
        Selector = selector;
        Source = source;
        AsText = asText;
        Location = location;
    }

    internal SqlBoundExpression Operand { get; }
    internal SqlBoundExpression Selector { get; }
    internal DatabaseType Source { get; }
    internal bool AsText { get; }
    internal Location? Location { get; }

    private sealed class DebugView
    {
        public DebugView(SqlBoundJsonAccess value) { _ = value; }
    }
}
