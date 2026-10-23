namespace Assimalign.Cohesion.Database.Sql.Internal;

/// <summary>A reference bound to an enclosing scope and its row expression.</summary>
[global::System.Diagnostics.DebuggerDisplay("SqlBoundOuterColumn")]
[global::System.Diagnostics.DebuggerTypeProxy(typeof(global::Assimalign.Cohesion.Database.Sql.Internal.SqlBoundOuterColumn.DebugView))]
internal sealed class SqlBoundOuterColumn : SqlBoundExpression
{
    internal SqlBoundOuterColumn(SqlOuterReference reference) : base(SqlBoundExpressionKind.OuterColumn)
    {
        Reference = reference;
    }

    internal SqlOuterReference Reference { get; }

    private sealed class DebugView
    {
        public DebugView(global::Assimalign.Cohesion.Database.Sql.Internal.SqlBoundOuterColumn value)
        {
            _ = value;
        }
    }
}
