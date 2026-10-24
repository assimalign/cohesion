namespace Assimalign.Cohesion.Database.Sql.Internal;

/// <summary>Drops a SQL namespace and optionally its dependent objects.</summary>
[global::System.Diagnostics.DebuggerDisplay("SqlDropSchemaPlan {Name,nq}")]
[global::System.Diagnostics.DebuggerTypeProxy(typeof(global::Assimalign.Cohesion.Database.Sql.Internal.SqlDropSchemaPlan.DebugView))]
internal sealed record SqlDropSchemaPlan(string Name, bool IfExists, bool Cascade) : SqlPlan{
    private sealed class DebugView
    {
        private readonly global::Assimalign.Cohesion.Database.Sql.Internal.SqlDropSchemaPlan _target;

        public DebugView(global::Assimalign.Cohesion.Database.Sql.Internal.SqlDropSchemaPlan value)
        {
            _target = value;
        }

        public string Name => _target.Name;
        public bool IfExists => _target.IfExists;
        public bool Cascade => _target.Cascade;
    }
}
