using Assimalign.Cohesion.Database.Sql.Language;

namespace Assimalign.Cohesion.Database.Sql.Internal;

[global::System.Diagnostics.DebuggerDisplay("SqlSequencePlan")]
[global::System.Diagnostics.DebuggerTypeProxy(typeof(DebugView))]
internal sealed record SqlSequencePlan(SqlQueryExpression Expression) : SqlPlan
{
    private sealed class DebugView
    {
        private readonly SqlSequencePlan _plan;
        public DebugView(SqlSequencePlan plan) { _plan = plan; }
        public SqlQueryExpression Expression => _plan.Expression;
    }
}
