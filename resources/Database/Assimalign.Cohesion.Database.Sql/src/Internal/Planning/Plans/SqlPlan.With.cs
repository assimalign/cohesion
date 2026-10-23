using System.Collections.Generic;

namespace Assimalign.Cohesion.Database.Sql.Internal;

/// <summary>Materializes the reachable WITH definitions before the statement's first write.</summary>
[global::System.Diagnostics.DebuggerDisplay("SqlWithPlan")]
[global::System.Diagnostics.DebuggerTypeProxy(typeof(global::Assimalign.Cohesion.Database.Sql.Internal.SqlWithPlan.DebugView))]
internal sealed record SqlWithPlan(SqlPlan Input, IReadOnlyList<SqlDerivedRelation> Relations) : SqlPlan{
    private sealed class DebugView
    {
        private readonly global::Assimalign.Cohesion.Database.Sql.Internal.SqlWithPlan _target;

        public DebugView(global::Assimalign.Cohesion.Database.Sql.Internal.SqlWithPlan value)
        {
            _target = value;
        }

        public global::Assimalign.Cohesion.Database.Sql.Internal.SqlPlan Input => _target.Input;
        public global::System.Collections.Generic.IReadOnlyList<global::Assimalign.Cohesion.Database.Sql.Internal.SqlDerivedRelation> Relations => _target.Relations;
    }
}
