using Assimalign.Cohesion.Database.Sql.Language;

namespace Assimalign.Cohesion.Database.Sql.Internal;

/// <summary>One written join step; comma items introduce unconditional CROSS steps.</summary>
[global::System.Diagnostics.DebuggerDisplay("SqlJoinStep")]
[global::System.Diagnostics.DebuggerTypeProxy(typeof(global::Assimalign.Cohesion.Database.Sql.Internal.SqlJoinStep.DebugView))]
internal sealed record SqlJoinStep(int Binding, SqlJoinType Kind, SqlBoundExpression? Condition, SqlJoinIndexPath? Access)
{
    internal SqlHashJoinPath? Hash { get; init; }

    private sealed class DebugView
    {
        private readonly global::Assimalign.Cohesion.Database.Sql.Internal.SqlJoinStep _target;

        public DebugView(global::Assimalign.Cohesion.Database.Sql.Internal.SqlJoinStep value)
        {
            _target = value;
        }

        public int Binding => _target.Binding;
        public global::Assimalign.Cohesion.Database.Sql.Language.SqlJoinType Kind => _target.Kind;
        public global::Assimalign.Cohesion.Database.Sql.Internal.SqlBoundExpression? Condition => _target.Condition;
        public global::Assimalign.Cohesion.Database.Sql.Internal.SqlJoinIndexPath? Access => _target.Access;
    }
}
