using System.Collections.Generic;

using Assimalign.Cohesion.Database.Sql.Catalog;
using Assimalign.Cohesion.Database.Sql.Language;
using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Sql.Internal;

/// <summary>Ordered partition evaluation between HAVING and final output processing.</summary>
[System.Diagnostics.DebuggerDisplay("SqlWindowAggregatePlan")]
[System.Diagnostics.DebuggerTypeProxy(typeof(SqlWindowAggregatePlan.DebugView))]
internal sealed record SqlWindowAggregatePlan(SqlPlan Input, IReadOnlyList<SqlCatalogColumn> Columns,
    IReadOnlyList<SqlRelationBinding>? Bindings, int InputWidth, IReadOnlyList<SqlWindowGroup> Groups,
    IReadOnlyList<SqlProjection> Projections, IReadOnlyList<SqlBoundOrdering> OrderBy,
    SqlRowCount? Limit, SqlRowCount? Offset, bool IsDistinct,
    IReadOnlyDictionary<SqlExpression, int>? OrderByProjections) : SqlRelationPlan
{
    internal override IReadOnlyList<SqlProjection> OutputProjections => Projections;
    internal override IReadOnlyList<SqlCatalogColumn> OutputColumns => Columns;
    internal override IReadOnlyList<SqlRelationBinding>? OutputBindings => Bindings;

    private sealed class DebugView
    {
        public DebugView(SqlWindowAggregatePlan value) { _ = value; }
    }
}

internal sealed record SqlWindowFunction(SqlWindowExpression Expression, string Name, int Ordinal,
    IReadOnlyList<SqlBoundExpression> Arguments, DatabaseType Type);

[System.Diagnostics.DebuggerDisplay("SqlWindowGroup")]
[System.Diagnostics.DebuggerTypeProxy(typeof(SqlWindowGroup.DebugView))]
internal sealed record SqlWindowGroup(IReadOnlyList<SqlExpression> PartitionSyntax,
    IReadOnlyList<SqlOrderByColumn> OrderSyntax, IReadOnlyList<SqlBoundOrdering> Partition,
    IReadOnlyList<SqlBoundOrdering> Ordering, List<SqlWindowFunction> Functions)
{
    internal bool SortShared { get; set; }

    private sealed class DebugView
    {
        public DebugView(SqlWindowGroup value) { _ = value; }
    }
}
