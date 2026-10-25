namespace Assimalign.Cohesion.Database.Sql.Internal;

[global::System.Diagnostics.DebuggerDisplay("SqlDropTablePlan {Name,nq}")]
[global::System.Diagnostics.DebuggerTypeProxy(typeof(global::Assimalign.Cohesion.Database.Sql.Internal.SqlDropTablePlan.DebugView))]
internal sealed record SqlDropTablePlan(string Schema, string Name, bool IfExists) : SqlPlan{
    private sealed class DebugView
    {
        private readonly global::Assimalign.Cohesion.Database.Sql.Internal.SqlDropTablePlan _target;

        public DebugView(global::Assimalign.Cohesion.Database.Sql.Internal.SqlDropTablePlan value)
        {
            _target = value;
        }

        public string Schema => _target.Schema;
        public string Name => _target.Name;
        public bool IfExists => _target.IfExists;
    }
}
