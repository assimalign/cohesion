using System.Collections.Generic;

namespace Assimalign.Cohesion.Database.Sql.Internal;

/// <summary>
/// Materializes uncorrelated child queries under the enclosing statement's snapshot,
/// then runs the bound input with those values available to scalar evaluation.
/// </summary>
/// <remarks>
/// The planner lowers each subquery's use site to a slot of the bound expression tree
/// (<see cref="SqlBoundSubquery"/>, <see cref="SqlBoundInSubquery"/>), numbered by the
/// binding that produces its values. The executor stores each child's values under that
/// number in the statement's <see cref="SqlSubqueryValues"/> before the input runs, so no
/// expression tree is rebuilt with the values in it: that would mean reconstructing the
/// language package's AST nodes from here, which needs their internal constructors.
/// </remarks>
[global::System.Diagnostics.DebuggerDisplay("SqlSubqueryPlan")]
[global::System.Diagnostics.DebuggerTypeProxy(typeof(global::Assimalign.Cohesion.Database.Sql.Internal.SqlSubqueryPlan.DebugView))]
internal sealed record SqlSubqueryPlan(SqlPlan Input, IReadOnlyList<SqlSubqueryBinding> Queries) : SqlPlan{
    private sealed class DebugView
    {
        private readonly global::Assimalign.Cohesion.Database.Sql.Internal.SqlSubqueryPlan _target;

        public DebugView(global::Assimalign.Cohesion.Database.Sql.Internal.SqlSubqueryPlan value)
        {
            _target = value;
        }

        public global::Assimalign.Cohesion.Database.Sql.Internal.SqlPlan Input => _target.Input;
        public global::System.Collections.Generic.IReadOnlyList<global::Assimalign.Cohesion.Database.Sql.Internal.SqlSubqueryBinding> Queries => _target.Queries;
    }
}
