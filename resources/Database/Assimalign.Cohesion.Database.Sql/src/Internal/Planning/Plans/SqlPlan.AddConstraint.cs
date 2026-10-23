using Assimalign.Cohesion.Database.Sql.Catalog;
using Assimalign.Cohesion.Database.Sql.Language;

namespace Assimalign.Cohesion.Database.Sql.Internal;

[global::System.Diagnostics.DebuggerDisplay("SqlAddConstraintPlan")]
[global::System.Diagnostics.DebuggerTypeProxy(typeof(global::Assimalign.Cohesion.Database.Sql.Internal.SqlAddConstraintPlan.DebugView))]
internal sealed record SqlAddConstraintPlan(SqlCatalogTable Table, SqlConstraintDefinition Constraint) : SqlPlan{
    private sealed class DebugView
    {
        private readonly global::Assimalign.Cohesion.Database.Sql.Internal.SqlAddConstraintPlan _target;

        public DebugView(global::Assimalign.Cohesion.Database.Sql.Internal.SqlAddConstraintPlan value)
        {
            _target = value;
        }

        public global::Assimalign.Cohesion.Database.Sql.Catalog.SqlCatalogTable Table => _target.Table;
        public global::Assimalign.Cohesion.Database.Sql.Language.SqlConstraintDefinition Constraint => _target.Constraint;
    }
}
