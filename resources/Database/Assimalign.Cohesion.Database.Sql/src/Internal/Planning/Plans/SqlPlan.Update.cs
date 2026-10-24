using System.Collections.Generic;

using Assimalign.Cohesion.Database.Sql.Catalog;

namespace Assimalign.Cohesion.Database.Sql.Internal;

[global::System.Diagnostics.DebuggerDisplay("SqlUpdatePlan")]
[global::System.Diagnostics.DebuggerTypeProxy(typeof(global::Assimalign.Cohesion.Database.Sql.Internal.SqlUpdatePlan.DebugView))]
internal sealed record SqlUpdatePlan(
    SqlCatalogTable Table,
    IReadOnlyList<(int Ordinal, SqlBoundExpression Value)> Assignments,
    SqlBoundExpression? Where,
    SqlAccessPath Access) : SqlPlan
{
    public IReadOnlyList<SqlProjection>? Returning { get; init; }

    private sealed class DebugView
    {
        private readonly global::Assimalign.Cohesion.Database.Sql.Internal.SqlUpdatePlan _target;

        public DebugView(global::Assimalign.Cohesion.Database.Sql.Internal.SqlUpdatePlan value)
        {
            _target = value;
        }

        public global::Assimalign.Cohesion.Database.Sql.Catalog.SqlCatalogTable Table => _target.Table;
        public global::System.Collections.Generic.IReadOnlyList<(int Ordinal, global::Assimalign.Cohesion.Database.Sql.Internal.SqlBoundExpression Value)> Assignments => _target.Assignments;
        public global::Assimalign.Cohesion.Database.Sql.Internal.SqlBoundExpression? Where => _target.Where;
        public global::Assimalign.Cohesion.Database.Sql.Internal.SqlAccessPath Access => _target.Access;
        public global::System.Collections.Generic.IReadOnlyList<global::Assimalign.Cohesion.Database.Sql.Internal.SqlProjection>? Returning => _target.Returning;
    }
}
