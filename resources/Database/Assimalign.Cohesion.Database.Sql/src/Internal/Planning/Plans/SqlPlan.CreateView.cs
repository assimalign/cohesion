using Assimalign.Cohesion.Database.Sql.Catalog;

namespace Assimalign.Cohesion.Database.Sql.Internal;

/// <summary>Publishes a canonical read-only view.</summary>
[global::System.Diagnostics.DebuggerDisplay("SqlCreateViewPlan")]
[global::System.Diagnostics.DebuggerTypeProxy(typeof(global::Assimalign.Cohesion.Database.Sql.Internal.SqlCreateViewPlan.DebugView))]
internal sealed record SqlCreateViewPlan(SqlCatalogView View, bool OrReplace) : SqlPlan{
    private sealed class DebugView
    {
        private readonly global::Assimalign.Cohesion.Database.Sql.Internal.SqlCreateViewPlan _target;

        public DebugView(global::Assimalign.Cohesion.Database.Sql.Internal.SqlCreateViewPlan value)
        {
            _target = value;
        }

        public global::Assimalign.Cohesion.Database.Sql.Catalog.SqlCatalogView View => _target.View;
        public bool OrReplace => _target.OrReplace;
    }
}
