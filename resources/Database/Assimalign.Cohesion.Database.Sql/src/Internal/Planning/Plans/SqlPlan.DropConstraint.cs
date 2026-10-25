using Assimalign.Cohesion.Database.Sql.Catalog;

namespace Assimalign.Cohesion.Database.Sql.Internal;

[global::System.Diagnostics.DebuggerDisplay("SqlDropConstraintPlan")]
[global::System.Diagnostics.DebuggerTypeProxy(typeof(global::Assimalign.Cohesion.Database.Sql.Internal.SqlDropConstraintPlan.DebugView))]
internal sealed record SqlDropConstraintPlan(SqlCatalogTable Table, string ConstraintName) : SqlPlan{
    private sealed class DebugView
    {
        private readonly global::Assimalign.Cohesion.Database.Sql.Internal.SqlDropConstraintPlan _target;

        public DebugView(global::Assimalign.Cohesion.Database.Sql.Internal.SqlDropConstraintPlan value)
        {
            _target = value;
        }

        public global::Assimalign.Cohesion.Database.Sql.Catalog.SqlCatalogTable Table => _target.Table;
        public string ConstraintName => _target.ConstraintName;
    }
}
