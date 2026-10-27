using Assimalign.Cohesion.Database.Sql.Catalog;

namespace Assimalign.Cohesion.Database.Sql.Internal;

[global::System.Diagnostics.DebuggerDisplay("SqlAlterColumnPlan")]
[global::System.Diagnostics.DebuggerTypeProxy(typeof(global::Assimalign.Cohesion.Database.Sql.Internal.SqlAlterColumnPlan.DebugView))]
internal sealed record SqlAlterColumnPlan(SqlCatalogTable Table, SqlCatalogColumn Column, bool AllowDefaultChange = false,
    SqlBoundExpression? Conversion = null) : SqlPlan{
    private sealed class DebugView
    {
        private readonly global::Assimalign.Cohesion.Database.Sql.Internal.SqlAlterColumnPlan _target;

        public DebugView(global::Assimalign.Cohesion.Database.Sql.Internal.SqlAlterColumnPlan value)
        {
            _target = value;
        }

        public global::Assimalign.Cohesion.Database.Sql.Catalog.SqlCatalogTable Table => _target.Table;
        public global::Assimalign.Cohesion.Database.Sql.Catalog.SqlCatalogColumn Column => _target.Column;
        public bool AllowDefaultChange => _target.AllowDefaultChange;
    }
}
