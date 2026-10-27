namespace Assimalign.Cohesion.Database.Sql.Internal;

/// <summary>Drops a persisted view and optionally its dependent views.</summary>
[global::System.Diagnostics.DebuggerDisplay("SqlDropViewPlan {Name,nq}")]
[global::System.Diagnostics.DebuggerTypeProxy(typeof(global::Assimalign.Cohesion.Database.Sql.Internal.SqlDropViewPlan.DebugView))]
internal sealed record SqlDropViewPlan(string Schema, string Name, bool IfExists, bool Cascade) : SqlPlan{
    private sealed class DebugView
    {
        private readonly global::Assimalign.Cohesion.Database.Sql.Internal.SqlDropViewPlan _target;

        public DebugView(global::Assimalign.Cohesion.Database.Sql.Internal.SqlDropViewPlan value)
        {
            _target = value;
        }

        public string Schema => _target.Schema;
        public string Name => _target.Name;
        public bool IfExists => _target.IfExists;
        public bool Cascade => _target.Cascade;
    }
}
