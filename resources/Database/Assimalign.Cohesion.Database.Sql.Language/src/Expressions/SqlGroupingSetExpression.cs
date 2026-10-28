using System.Collections.Generic;

using Assimalign.Cohesion.Database.Language;

namespace Assimalign.Cohesion.Database.Sql.Language;

/// <summary>Represents a grouping element in GROUP BY, including an empty or composite set.</summary>
public sealed class SqlGroupingSetExpression : SqlExpression
{
    internal SqlGroupingSetExpression(SqlGroupingSetKind kind, IReadOnlyList<SqlExpression> items, Location? location)
        : base(location)
    {
        Kind = kind;
        Items = items;
        Depth = 1 + DepthOf(items);
    }

    /// <summary>Gets the grouping operation.</summary>
    public SqlGroupingSetKind Kind { get; }

    /// <summary>Gets the ordered child expressions or grouping elements; an empty composite denotes ().</summary>
    public IReadOnlyList<SqlExpression> Items { get; }
}
