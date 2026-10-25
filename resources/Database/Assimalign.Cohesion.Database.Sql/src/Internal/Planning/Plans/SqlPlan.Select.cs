using System.Collections.Generic;

using Assimalign.Cohesion.Database.Sql.Catalog;
using Assimalign.Cohesion.Database.Sql.Language;

namespace Assimalign.Cohesion.Database.Sql.Internal;

[global::System.Diagnostics.DebuggerDisplay("SqlSelectPlan")]
[global::System.Diagnostics.DebuggerTypeProxy(typeof(global::Assimalign.Cohesion.Database.Sql.Internal.SqlSelectPlan.DebugView))]
internal sealed record SqlSelectPlan(
    SqlCatalogTable Table,
    IReadOnlyList<SqlProjection> Projections,
    SqlBoundExpression? Where,
    IReadOnlyList<SqlBoundOrdering> OrderBy,
    SqlRowCount? Limit,
    SqlRowCount? Offset,
    bool IsDistinct,
    SqlAccessPath Access,
    IReadOnlyDictionary<SqlExpression, int>? OrderByProjections = null) : SqlRelationPlan
{
    internal override IReadOnlyList<SqlProjection> OutputProjections => Projections;
    internal override IReadOnlyList<SqlCatalogColumn> OutputColumns => Table.Columns;

    private sealed class DebugView
    {
        private readonly global::Assimalign.Cohesion.Database.Sql.Internal.SqlSelectPlan _target;

        public DebugView(global::Assimalign.Cohesion.Database.Sql.Internal.SqlSelectPlan value)
        {
            _target = value;
        }

        public global::Assimalign.Cohesion.Database.Sql.Catalog.SqlCatalogTable Table => _target.Table;
        public global::System.Collections.Generic.IReadOnlyList<global::Assimalign.Cohesion.Database.Sql.Internal.SqlProjection> Projections => _target.Projections;
        public global::Assimalign.Cohesion.Database.Sql.Internal.SqlBoundExpression? Where => _target.Where;
        public global::System.Collections.Generic.IReadOnlyList<global::Assimalign.Cohesion.Database.Sql.Internal.SqlBoundOrdering> OrderBy => _target.OrderBy;
        public global::Assimalign.Cohesion.Database.Sql.Internal.SqlRowCount? Limit => _target.Limit;
        public global::Assimalign.Cohesion.Database.Sql.Internal.SqlRowCount? Offset => _target.Offset;
        public bool IsDistinct => _target.IsDistinct;
        public global::Assimalign.Cohesion.Database.Sql.Internal.SqlAccessPath Access => _target.Access;
        public global::System.Collections.Generic.IReadOnlyDictionary<global::Assimalign.Cohesion.Database.Sql.Language.SqlExpression, int>? OrderByProjections => _target.OrderByProjections;
    }
}
