using Assimalign.Cohesion.Database.Sql.Language;

namespace Assimalign.Cohesion.Database.Sql.Internal;

[global::System.Diagnostics.DebuggerDisplay("Truncate")]
[global::System.Diagnostics.DebuggerTypeProxy(typeof(SqlTruncatePlan.DebugView))]
internal sealed record SqlTruncatePlan(SqlTruncateExpression Expression) : SqlPlan
{
    private sealed class DebugView
    {
        private readonly SqlTruncatePlan _target;

        public DebugView(SqlTruncatePlan target) { _target = target; }

        public SqlTruncateExpression Expression => _target.Expression;
    }
}
