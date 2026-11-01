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
        private readonly SqlWindowAggregatePlan _value;
        public DebugView(SqlWindowAggregatePlan value) { _value = value; }
        public SqlPlan Input => _value.Input;
        public IReadOnlyList<SqlCatalogColumn> Columns => _value.Columns;
        public IReadOnlyList<SqlRelationBinding>? Bindings => _value.Bindings;
        public int InputWidth => _value.InputWidth;
        public IReadOnlyList<SqlWindowGroup> Groups => _value.Groups;
        public IReadOnlyList<SqlProjection> Projections => _value.Projections;
        public IReadOnlyList<SqlBoundOrdering> OrderBy => _value.OrderBy;
        public SqlRowCount? Limit => _value.Limit;
        public SqlRowCount? Offset => _value.Offset;
        public bool IsDistinct => _value.IsDistinct;
        public IReadOnlyDictionary<SqlExpression, int>? OrderByProjections => _value.OrderByProjections;
    }
}

internal sealed record SqlWindowFunction(SqlWindowExpression Expression, string Name, int Ordinal,
    IReadOnlyList<SqlBoundExpression> Arguments, DatabaseType Type, SqlWindowFrameBinding? Frame, SqlGroupAggregate? Aggregate);

internal sealed record SqlWindowFrameBinding(SqlWindowFrame Syntax, SqlBoundExpression? StartOffset, SqlBoundExpression? EndOffset);

[System.Diagnostics.DebuggerDisplay("SqlWindowGroup")]
[System.Diagnostics.DebuggerTypeProxy(typeof(SqlWindowGroup.DebugView))]
internal sealed record SqlWindowGroup(IReadOnlyList<SqlExpression> PartitionSyntax,
    IReadOnlyList<SqlOrderByColumn> OrderSyntax, IReadOnlyList<SqlBoundOrdering> Partition,
    IReadOnlyList<SqlBoundOrdering> Ordering, List<SqlWindowFunction> Functions)
{
    internal bool SortShared { get; set; }

    private sealed class DebugView
    {
        private readonly SqlWindowGroup _value;
        public DebugView(SqlWindowGroup value) { _value = value; }
        public IReadOnlyList<SqlExpression> PartitionSyntax => _value.PartitionSyntax;
        public IReadOnlyList<SqlOrderByColumn> OrderSyntax => _value.OrderSyntax;
        public IReadOnlyList<SqlBoundOrdering> Partition => _value.Partition;
        public IReadOnlyList<SqlBoundOrdering> Ordering => _value.Ordering;
        public List<SqlWindowFunction> Functions => _value.Functions;
    }
}
