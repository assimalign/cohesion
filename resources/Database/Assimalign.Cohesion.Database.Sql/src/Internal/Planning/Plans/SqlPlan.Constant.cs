using System.Collections.Generic;

using Assimalign.Cohesion.Database.Sql.Catalog;
using Assimalign.Cohesion.Database.Sql.Language;

namespace Assimalign.Cohesion.Database.Sql.Internal;

/// <summary>The singleton input of a scalar SELECT body with no FROM clause.</summary>
[global::System.Diagnostics.DebuggerDisplay("SqlConstantPlan")]
[global::System.Diagnostics.DebuggerTypeProxy(typeof(global::Assimalign.Cohesion.Database.Sql.Internal.SqlConstantPlan.DebugView))]
internal sealed record SqlConstantPlan(IReadOnlyList<SqlProjection> Projections, SqlBoundExpression? Where,
    IReadOnlyList<SqlBoundOrdering> OrderBy, SqlRowCount? Limit, SqlRowCount? Offset, bool IsDistinct,
    IReadOnlyDictionary<SqlExpression, int>? OrderByProjections) : SqlRelationPlan
{
    internal override IReadOnlyList<SqlProjection> OutputProjections => Projections;
    internal override IReadOnlyList<SqlCatalogColumn> OutputColumns => [];

    private sealed class DebugView
    {
        private readonly global::Assimalign.Cohesion.Database.Sql.Internal.SqlConstantPlan _target;

        public DebugView(global::Assimalign.Cohesion.Database.Sql.Internal.SqlConstantPlan value)
        {
            _target = value;
        }

        public global::System.Collections.Generic.IReadOnlyList<global::Assimalign.Cohesion.Database.Sql.Internal.SqlProjection> Projections => _target.Projections;
        public global::Assimalign.Cohesion.Database.Sql.Internal.SqlBoundExpression? Where => _target.Where;
        public global::System.Collections.Generic.IReadOnlyList<global::Assimalign.Cohesion.Database.Sql.Internal.SqlBoundOrdering> OrderBy => _target.OrderBy;
        public global::Assimalign.Cohesion.Database.Sql.Internal.SqlRowCount? Limit => _target.Limit;
        public global::Assimalign.Cohesion.Database.Sql.Internal.SqlRowCount? Offset => _target.Offset;
        public bool IsDistinct => _target.IsDistinct;
        public global::System.Collections.Generic.IReadOnlyDictionary<global::Assimalign.Cohesion.Database.Sql.Language.SqlExpression, int>? OrderByProjections => _target.OrderByProjections;
    }
}
