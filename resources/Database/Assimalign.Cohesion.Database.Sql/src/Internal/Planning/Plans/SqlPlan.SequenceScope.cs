namespace Assimalign.Cohesion.Database.Sql.Internal;

// Binding metadata only: the session unwraps this before costing/execution. Ordinary plans
// retain their existing fields and allocation size. EXPLAIN exposes the scope as a parent.
[global::System.Diagnostics.DebuggerDisplay("SqlSequenceScopePlan")]
[global::System.Diagnostics.DebuggerTypeProxy(typeof(DebugView))]
internal sealed record SqlSequenceScopePlan(SqlPlan Input, SqlSequenceUsage Usage) : SqlPlan
{
    private sealed class DebugView
    {
        private readonly SqlSequenceScopePlan _plan;
        public DebugView(SqlSequenceScopePlan plan) { _plan = plan; }
        public SqlPlan Input => _plan.Input;
        public SqlSequenceUsage Usage => _plan.Usage;
    }
}
