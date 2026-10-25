namespace Assimalign.Cohesion.Database.Sql.Internal;

/// <summary>Creates an empty SQL namespace.</summary>
[global::System.Diagnostics.DebuggerDisplay("SqlCreateSchemaPlan {Name,nq}")]
[global::System.Diagnostics.DebuggerTypeProxy(typeof(global::Assimalign.Cohesion.Database.Sql.Internal.SqlCreateSchemaPlan.DebugView))]
internal sealed record SqlCreateSchemaPlan(string Name, bool IfNotExists) : SqlPlan{
    private sealed class DebugView
    {
        private readonly global::Assimalign.Cohesion.Database.Sql.Internal.SqlCreateSchemaPlan _target;

        public DebugView(global::Assimalign.Cohesion.Database.Sql.Internal.SqlCreateSchemaPlan value)
        {
            _target = value;
        }

        public string Name => _target.Name;
        public bool IfNotExists => _target.IfNotExists;
    }
}
