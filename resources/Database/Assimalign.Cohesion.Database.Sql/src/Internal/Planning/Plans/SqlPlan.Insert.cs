using System.Collections.Generic;

using Assimalign.Cohesion.Database.Sql.Catalog;

namespace Assimalign.Cohesion.Database.Sql.Internal;

/// <summary>An <c>INSERT ... VALUES</c>: each row's values bound with no columns in scope.</summary>
[global::System.Diagnostics.DebuggerDisplay("SqlInsertPlan")]
[global::System.Diagnostics.DebuggerTypeProxy(typeof(global::Assimalign.Cohesion.Database.Sql.Internal.SqlInsertPlan.DebugView))]
internal sealed record SqlInsertPlan(
    SqlCatalogTable Table,
    IReadOnlyList<int> TargetOrdinals,
    IReadOnlyList<SqlBoundExpression[]> Rows) : SqlPlan
{
    public IReadOnlyList<SqlProjection>? Returning { get; init; }
    public SqlOnConflictPlan? OnConflict { get; init; }

    private sealed class DebugView
    {
        private readonly global::Assimalign.Cohesion.Database.Sql.Internal.SqlInsertPlan _target;

        public DebugView(global::Assimalign.Cohesion.Database.Sql.Internal.SqlInsertPlan value)
        {
            _target = value;
        }

        public global::Assimalign.Cohesion.Database.Sql.Catalog.SqlCatalogTable Table => _target.Table;
        public global::System.Collections.Generic.IReadOnlyList<int> TargetOrdinals => _target.TargetOrdinals;
        public global::System.Collections.Generic.IReadOnlyList<global::Assimalign.Cohesion.Database.Sql.Internal.SqlBoundExpression[]> Rows => _target.Rows;
        public global::System.Collections.Generic.IReadOnlyList<global::Assimalign.Cohesion.Database.Sql.Internal.SqlProjection>? Returning => _target.Returning;
        public global::Assimalign.Cohesion.Database.Sql.Internal.SqlOnConflictPlan? OnConflict => _target.OnConflict;
    }
}
