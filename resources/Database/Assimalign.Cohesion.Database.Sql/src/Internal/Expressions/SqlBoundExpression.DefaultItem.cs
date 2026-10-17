namespace Assimalign.Cohesion.Database.Sql.Internal;

/// <summary>
/// A planner-resolved request to omit this target's value and use the table version's default.
/// It is consumed by assignment execution and is never evaluated as a scalar expression.
/// </summary>
internal sealed class SqlBoundDefaultItem : SqlBoundExpression
{
    internal static readonly SqlBoundDefaultItem Instance = new();

    private SqlBoundDefaultItem() : base(SqlBoundExpressionKind.DefaultItem)
    {
    }
}
