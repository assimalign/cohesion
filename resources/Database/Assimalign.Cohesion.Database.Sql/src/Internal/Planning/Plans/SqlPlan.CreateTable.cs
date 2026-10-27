using System.Collections.Generic;

using Assimalign.Cohesion.Database.Sql.Catalog;
using Assimalign.Cohesion.Database.Sql.Language;

namespace Assimalign.Cohesion.Database.Sql.Internal;

[global::System.Diagnostics.DebuggerDisplay("SqlCreateTablePlan {Name,nq}")]
[global::System.Diagnostics.DebuggerTypeProxy(typeof(global::Assimalign.Cohesion.Database.Sql.Internal.SqlCreateTablePlan.DebugView))]
internal sealed record SqlCreateTablePlan(
    string Schema,
    string Name,
    IReadOnlyList<SqlCatalogColumn> Columns,
    IReadOnlyList<string> PrimaryKey,
    bool IfNotExists,
    IReadOnlyList<SqlConstraintDefinition> Constraints) : SqlPlan{
    private sealed class DebugView
    {
        private readonly global::Assimalign.Cohesion.Database.Sql.Internal.SqlCreateTablePlan _target;

        public DebugView(global::Assimalign.Cohesion.Database.Sql.Internal.SqlCreateTablePlan value)
        {
            _target = value;
        }

        public string Schema => _target.Schema;
        public string Name => _target.Name;
        public global::System.Collections.Generic.IReadOnlyList<global::Assimalign.Cohesion.Database.Sql.Catalog.SqlCatalogColumn> Columns => _target.Columns;
        public global::System.Collections.Generic.IReadOnlyList<string> PrimaryKey => _target.PrimaryKey;
        public bool IfNotExists => _target.IfNotExists;
        public global::System.Collections.Generic.IReadOnlyList<global::Assimalign.Cohesion.Database.Sql.Language.SqlConstraintDefinition> Constraints => _target.Constraints;
    }
}
