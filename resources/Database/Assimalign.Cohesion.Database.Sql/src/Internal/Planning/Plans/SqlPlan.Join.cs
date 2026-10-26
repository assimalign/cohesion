using System.Collections.Generic;

using Assimalign.Cohesion.Database.Sql.Catalog;
using Assimalign.Cohesion.Database.Sql.Language;

namespace Assimalign.Cohesion.Database.Sql.Internal;

/// <summary>
/// A left-deep N-relation join. Physical slots retain FROM-then-JOIN order even
/// when the selected index reverses which relation drives the nested loop.
/// </summary>
[global::System.Diagnostics.DebuggerDisplay("SqlJoinPlan")]
[global::System.Diagnostics.DebuggerTypeProxy(typeof(global::Assimalign.Cohesion.Database.Sql.Internal.SqlJoinPlan.DebugView))]
internal sealed record SqlJoinPlan(
    IReadOnlyList<SqlRelationBinding> Bindings,
    IReadOnlyList<SqlCatalogColumn> Columns,
    IReadOnlyList<SqlJoinStep> Steps,
    IReadOnlyList<SqlProjection> Projections,
    SqlBoundExpression? Where,
    IReadOnlyList<SqlBoundOrdering> OrderBy,
    SqlRowCount? Limit,
    SqlRowCount? Offset,
    bool IsDistinct,
    IReadOnlyDictionary<SqlExpression, int>? OrderByProjections = null) : SqlRelationPlan
{
    /// <summary>Normalized physical tree; null retains the ordinary flat execution path.</summary>
    internal SqlJoinTree? Tree { get; init; }

    /// <summary>Restrictions evaluated before this binding contributes a candidate pair.</summary>
    internal IReadOnlyList<SqlJoinInput> Inputs { get; init; } = [];

    /// <summary>The driving input of the first pair, independent of runtime index attachment.</summary>
    internal int FirstBinding { get; init; }

    internal Assimalign.Cohesion.Database.Indexing.BTreeRangeCount?[]? InputEstimates { get; init; }

    internal override IReadOnlyList<SqlProjection> OutputProjections => Projections;
    internal override IReadOnlyList<SqlCatalogColumn> OutputColumns => Columns;
    internal override IReadOnlyList<SqlRelationBinding>? OutputBindings => Bindings;

    private sealed class DebugView
    {
        private readonly global::Assimalign.Cohesion.Database.Sql.Internal.SqlJoinPlan _target;

        public DebugView(global::Assimalign.Cohesion.Database.Sql.Internal.SqlJoinPlan value)
        {
            _target = value;
        }

        public global::System.Collections.Generic.IReadOnlyList<global::Assimalign.Cohesion.Database.Sql.Internal.SqlRelationBinding> Bindings => _target.Bindings;
        public global::System.Collections.Generic.IReadOnlyList<global::Assimalign.Cohesion.Database.Sql.Catalog.SqlCatalogColumn> Columns => _target.Columns;
        public global::System.Collections.Generic.IReadOnlyList<global::Assimalign.Cohesion.Database.Sql.Internal.SqlJoinStep> Steps => _target.Steps;
        public global::System.Collections.Generic.IReadOnlyList<global::Assimalign.Cohesion.Database.Sql.Internal.SqlProjection> Projections => _target.Projections;
        public global::Assimalign.Cohesion.Database.Sql.Internal.SqlBoundExpression? Where => _target.Where;
        public global::System.Collections.Generic.IReadOnlyList<global::Assimalign.Cohesion.Database.Sql.Internal.SqlBoundOrdering> OrderBy => _target.OrderBy;
        public global::Assimalign.Cohesion.Database.Sql.Internal.SqlRowCount? Limit => _target.Limit;
        public global::Assimalign.Cohesion.Database.Sql.Internal.SqlRowCount? Offset => _target.Offset;
        public bool IsDistinct => _target.IsDistinct;
        public global::System.Collections.Generic.IReadOnlyDictionary<global::Assimalign.Cohesion.Database.Sql.Language.SqlExpression, int>? OrderByProjections => _target.OrderByProjections;
    }
}
