using Assimalign.Cohesion.Database.Sql.Catalog;

namespace Assimalign.Cohesion.Database.Sql.Internal;

[global::System.Diagnostics.DebuggerDisplay("SqlRenamePlan")]
[global::System.Diagnostics.DebuggerTypeProxy(typeof(global::Assimalign.Cohesion.Database.Sql.Internal.SqlRenamePlan.DebugView))]
internal sealed record SqlRenamePlan(SqlCatalogTable Table, string? ColumnName, string NewName) : SqlPlan{
    private sealed class DebugView
    {
        private readonly global::Assimalign.Cohesion.Database.Sql.Internal.SqlRenamePlan _target;

        public DebugView(global::Assimalign.Cohesion.Database.Sql.Internal.SqlRenamePlan value)
        {
            _target = value;
        }

        public global::Assimalign.Cohesion.Database.Sql.Catalog.SqlCatalogTable Table => _target.Table;
        public string? ColumnName => _target.ColumnName;
        public string NewName => _target.NewName;
    }
}
