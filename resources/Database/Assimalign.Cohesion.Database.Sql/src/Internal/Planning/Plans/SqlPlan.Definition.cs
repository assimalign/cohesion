using Assimalign.Cohesion.Database.Sql.Language;

namespace Assimalign.Cohesion.Database.Sql.Internal;

/// <summary>A database-scoped SQL function or domain DDL statement.</summary>
[global::System.Diagnostics.DebuggerDisplay("SqlDefinitionPlan")]
[global::System.Diagnostics.DebuggerTypeProxy(typeof(global::Assimalign.Cohesion.Database.Sql.Internal.SqlDefinitionPlan.DebugView))]
internal sealed record SqlDefinitionPlan(SqlQueryExpression Expression) : SqlPlan{
    private sealed class DebugView
    {
        private readonly global::Assimalign.Cohesion.Database.Sql.Internal.SqlDefinitionPlan _target;

        public DebugView(global::Assimalign.Cohesion.Database.Sql.Internal.SqlDefinitionPlan value)
        {
            _target = value;
        }

        public global::Assimalign.Cohesion.Database.Sql.Language.SqlQueryExpression Expression => _target.Expression;
    }
}
