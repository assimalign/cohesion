using System.Collections.Generic;

using Assimalign.Cohesion.Database.Sql.Catalog;
using Assimalign.Cohesion.Database.Sql.Language;

namespace Assimalign.Cohesion.Database.Sql.Internal;

[global::System.Diagnostics.DebuggerDisplay("SqlAddColumnPlan {Name,nq}")]
[global::System.Diagnostics.DebuggerTypeProxy(typeof(global::Assimalign.Cohesion.Database.Sql.Internal.SqlAddColumnPlan.DebugView))]
internal sealed record SqlAddColumnPlan(string Schema, string Name, SqlCatalogColumn Column, IReadOnlyList<SqlConstraintDefinition> Constraints) : SqlPlan{
    private sealed class DebugView
    {
        private readonly global::Assimalign.Cohesion.Database.Sql.Internal.SqlAddColumnPlan _target;

        public DebugView(global::Assimalign.Cohesion.Database.Sql.Internal.SqlAddColumnPlan value)
        {
            _target = value;
        }

        public string Schema => _target.Schema;
        public string Name => _target.Name;
        public global::Assimalign.Cohesion.Database.Sql.Catalog.SqlCatalogColumn Column => _target.Column;
        public global::System.Collections.Generic.IReadOnlyList<global::Assimalign.Cohesion.Database.Sql.Language.SqlConstraintDefinition> Constraints => _target.Constraints;
    }
}
