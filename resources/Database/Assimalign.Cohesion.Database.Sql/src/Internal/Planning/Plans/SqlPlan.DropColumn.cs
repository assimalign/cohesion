namespace Assimalign.Cohesion.Database.Sql.Internal;

[global::System.Diagnostics.DebuggerDisplay("SqlDropColumnPlan {Name,nq}")]
[global::System.Diagnostics.DebuggerTypeProxy(typeof(global::Assimalign.Cohesion.Database.Sql.Internal.SqlDropColumnPlan.DebugView))]
internal sealed record SqlDropColumnPlan(string Schema, string Name, string ColumnName) : SqlPlan{
    private sealed class DebugView
    {
        private readonly global::Assimalign.Cohesion.Database.Sql.Internal.SqlDropColumnPlan _target;

        public DebugView(global::Assimalign.Cohesion.Database.Sql.Internal.SqlDropColumnPlan value)
        {
            _target = value;
        }

        public string Schema => _target.Schema;
        public string Name => _target.Name;
        public string ColumnName => _target.ColumnName;
    }
}
