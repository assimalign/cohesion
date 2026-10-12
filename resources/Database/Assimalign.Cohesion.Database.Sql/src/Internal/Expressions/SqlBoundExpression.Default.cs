using Assimalign.Cohesion.Database.Sql.Catalog;

namespace Assimalign.Cohesion.Database.Sql.Internal;

/// <summary>A row-independent default evaluated at assignment, with the column's coercion.</summary>
internal sealed class SqlBoundDefault : SqlBoundExpression
{
    internal SqlBoundDefault(SqlBoundExpression value, SqlCatalogColumn column) : base(SqlBoundExpressionKind.Default)
    {
        Value = value;
        Column = column;
    }
    internal SqlBoundExpression Value { get; }
    internal SqlCatalogColumn Column { get; }
}
