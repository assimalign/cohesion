using Assimalign.Cohesion.Database.Sql.Catalog;

namespace Assimalign.Cohesion.Database.Sql.Internal;

/// <summary>A row-independent default evaluated at assignment, with the column's coercion.</summary>
[global::System.Diagnostics.DebuggerDisplay("SqlBoundDefault")]
[global::System.Diagnostics.DebuggerTypeProxy(typeof(global::Assimalign.Cohesion.Database.Sql.Internal.SqlBoundDefault.DebugView))]
internal sealed class SqlBoundDefault : SqlBoundExpression
{
    internal SqlBoundDefault(SqlBoundExpression value, SqlCatalogColumn column, bool requiresSourceOrder = false) : base(SqlBoundExpressionKind.Default)
    {
        Value = value;
        Column = column;
        RequiresSourceOrder = requiresSourceOrder;
    }
    internal SqlBoundExpression Value { get; }
    internal SqlCatalogColumn Column { get; }
    internal bool RequiresSourceOrder { get; }

    private sealed class DebugView
    {
        public DebugView(global::Assimalign.Cohesion.Database.Sql.Internal.SqlBoundDefault value)
        {
            _ = value;
        }
    }
}
