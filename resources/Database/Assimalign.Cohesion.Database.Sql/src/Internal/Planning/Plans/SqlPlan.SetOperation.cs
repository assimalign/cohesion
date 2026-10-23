using System.Collections.Generic;

using Assimalign.Cohesion.Database.Sql.Catalog;
using Assimalign.Cohesion.Database.Sql.Language;

namespace Assimalign.Cohesion.Database.Sql.Internal;

/// <summary>A materialized set operation, with compatible UNION chains stored as N inputs.</summary>
[global::System.Diagnostics.DebuggerDisplay("SqlSetOperationPlan")]
[global::System.Diagnostics.DebuggerTypeProxy(typeof(global::Assimalign.Cohesion.Database.Sql.Internal.SqlSetOperationPlan.DebugView))]
internal sealed record SqlSetOperationPlan(
    List<SqlSetOperand> Operands, SqlSetOperator Operator, bool IsAll,
    IReadOnlyList<SqlProjection> Projections, IReadOnlyList<SqlCatalogColumn> Columns,
    IReadOnlyList<SqlBoundOrdering> OrderBy, SqlRowCount? Limit, SqlRowCount? Offset) : SqlRelationPlan
{
    internal override IReadOnlyList<SqlProjection> OutputProjections => Projections;
    internal override IReadOnlyList<SqlCatalogColumn> OutputColumns => Columns;

    private sealed class DebugView
    {
        private readonly global::Assimalign.Cohesion.Database.Sql.Internal.SqlSetOperationPlan _target;

        public DebugView(global::Assimalign.Cohesion.Database.Sql.Internal.SqlSetOperationPlan value)
        {
            _target = value;
        }

        public global::System.Collections.Generic.List<global::Assimalign.Cohesion.Database.Sql.Internal.SqlSetOperand> Operands => _target.Operands;
        public global::Assimalign.Cohesion.Database.Sql.Language.SqlSetOperator Operator => _target.Operator;
        public bool IsAll => _target.IsAll;
        public global::System.Collections.Generic.IReadOnlyList<global::Assimalign.Cohesion.Database.Sql.Internal.SqlProjection> Projections => _target.Projections;
        public global::System.Collections.Generic.IReadOnlyList<global::Assimalign.Cohesion.Database.Sql.Catalog.SqlCatalogColumn> Columns => _target.Columns;
        public global::System.Collections.Generic.IReadOnlyList<global::Assimalign.Cohesion.Database.Sql.Internal.SqlBoundOrdering> OrderBy => _target.OrderBy;
        public global::Assimalign.Cohesion.Database.Sql.Internal.SqlRowCount? Limit => _target.Limit;
        public global::Assimalign.Cohesion.Database.Sql.Internal.SqlRowCount? Offset => _target.Offset;
    }
}
