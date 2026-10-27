using System.Collections.Generic;

namespace Assimalign.Cohesion.Database.Sql.Internal;

/// <summary>Registers children that consume enclosing rows; each evaluation frame owns its value slots.</summary>
[global::System.Diagnostics.DebuggerDisplay("SqlCorrelatedSubqueryPlan")]
[global::System.Diagnostics.DebuggerTypeProxy(typeof(global::Assimalign.Cohesion.Database.Sql.Internal.SqlCorrelatedSubqueryPlan.DebugView))]
internal sealed record SqlCorrelatedSubqueryPlan(SqlPlan Input, IReadOnlyList<SqlSubqueryBinding> Queries) : SqlPlan{
    private sealed class DebugView
    {
        private readonly global::Assimalign.Cohesion.Database.Sql.Internal.SqlCorrelatedSubqueryPlan _target;

        public DebugView(global::Assimalign.Cohesion.Database.Sql.Internal.SqlCorrelatedSubqueryPlan value)
        {
            _target = value;
        }

        public global::Assimalign.Cohesion.Database.Sql.Internal.SqlPlan Input => _target.Input;
        public global::System.Collections.Generic.IReadOnlyList<global::Assimalign.Cohesion.Database.Sql.Internal.SqlSubqueryBinding> Queries => _target.Queries;
    }
}
