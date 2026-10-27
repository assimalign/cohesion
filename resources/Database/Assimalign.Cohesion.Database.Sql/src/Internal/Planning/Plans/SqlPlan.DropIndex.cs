using Assimalign.Cohesion.Database.Sql.Catalog;

namespace Assimalign.Cohesion.Database.Sql.Internal;

[global::System.Diagnostics.DebuggerDisplay("SqlDropIndexPlan")]
[global::System.Diagnostics.DebuggerTypeProxy(typeof(global::Assimalign.Cohesion.Database.Sql.Internal.SqlDropIndexPlan.DebugView))]
internal sealed record SqlDropIndexPlan(SqlCatalogTable Table, string IndexName, bool IfExists) : SqlPlan{
    private sealed class DebugView
    {
        private readonly global::Assimalign.Cohesion.Database.Sql.Internal.SqlDropIndexPlan _target;

        public DebugView(global::Assimalign.Cohesion.Database.Sql.Internal.SqlDropIndexPlan value)
        {
            _target = value;
        }

        public global::Assimalign.Cohesion.Database.Sql.Catalog.SqlCatalogTable Table => _target.Table;
        public string IndexName => _target.IndexName;
        public bool IfExists => _target.IfExists;
    }
}
