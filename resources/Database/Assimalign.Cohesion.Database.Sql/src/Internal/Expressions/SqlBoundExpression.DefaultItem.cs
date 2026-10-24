namespace Assimalign.Cohesion.Database.Sql.Internal;

/// <summary>
/// A planner-resolved request to omit this target's value and use the table version's default.
/// It is consumed by assignment execution and is never evaluated as a scalar expression.
/// </summary>
[global::System.Diagnostics.DebuggerDisplay("SqlBoundDefaultItem")]
[global::System.Diagnostics.DebuggerTypeProxy(typeof(global::Assimalign.Cohesion.Database.Sql.Internal.SqlBoundDefaultItem.DebugView))]
internal sealed class SqlBoundDefaultItem : SqlBoundExpression
{
    internal static readonly SqlBoundDefaultItem Instance = new();

    private SqlBoundDefaultItem() : base(SqlBoundExpressionKind.DefaultItem)
    {
    }

    private sealed class DebugView
    {
        public DebugView(global::Assimalign.Cohesion.Database.Sql.Internal.SqlBoundDefaultItem value)
        {
            _ = value;
        }
    }
}
