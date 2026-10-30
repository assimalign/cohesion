using System.Collections.Generic;

using Assimalign.Cohesion.Database.Sql.Catalog;

namespace Assimalign.Cohesion.Database.Sql.Internal;

[global::System.Diagnostics.DebuggerDisplay("SqlCreateIndexPlan")]
[global::System.Diagnostics.DebuggerTypeProxy(typeof(global::Assimalign.Cohesion.Database.Sql.Internal.SqlCreateIndexPlan.DebugView))]
internal sealed record SqlCreateIndexPlan(
    SqlCatalogTable Table,
    string IndexName,
    IReadOnlyList<string> ColumnNames,
    bool IsUnique,
    bool IfNotExists,
    IReadOnlyList<SqlCatalogIndexKeyOrder> KeyOrders,
    SqlCatalogIndex? PartialIndex = null) : SqlPlan{
    private sealed class DebugView
    {
        private readonly global::Assimalign.Cohesion.Database.Sql.Internal.SqlCreateIndexPlan _target;

        public DebugView(global::Assimalign.Cohesion.Database.Sql.Internal.SqlCreateIndexPlan value)
        {
            _target = value;
        }

        public global::Assimalign.Cohesion.Database.Sql.Catalog.SqlCatalogTable Table => _target.Table;
        public string IndexName => _target.IndexName;
        public global::System.Collections.Generic.IReadOnlyList<string> ColumnNames => _target.ColumnNames;
        public bool IsUnique => _target.IsUnique;
        public bool IfNotExists => _target.IfNotExists;
    }
}
