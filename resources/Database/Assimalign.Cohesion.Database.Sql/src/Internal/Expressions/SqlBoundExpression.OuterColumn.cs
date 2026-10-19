namespace Assimalign.Cohesion.Database.Sql.Internal;

/// <summary>A reference bound to an enclosing scope and its row expression.</summary>
internal sealed class SqlBoundOuterColumn : SqlBoundExpression
{
    internal SqlBoundOuterColumn(SqlOuterReference reference) : base(SqlBoundExpressionKind.OuterColumn)
    {
        Reference = reference;
    }

    internal SqlOuterReference Reference { get; }
}
