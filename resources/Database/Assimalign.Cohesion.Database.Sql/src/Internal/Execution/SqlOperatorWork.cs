namespace Assimalign.Cohesion.Database.Sql.Internal;

/// <summary>Analysis-only counter; absent during ordinary execution.</summary>
[global::System.Diagnostics.DebuggerDisplay("SqlOperatorWork")]
[global::System.Diagnostics.DebuggerTypeProxy(typeof(global::Assimalign.Cohesion.Database.Sql.Internal.SqlOperatorWork.DebugView))]
internal sealed class SqlOperatorWork
{
    internal long Candidates;
    internal long WindowTransitions;
    internal long WindowInverseTransitions;
    internal long WindowBoundaryVisits;

    private sealed class DebugView
    {
        public DebugView(global::Assimalign.Cohesion.Database.Sql.Internal.SqlOperatorWork value)
        {
            _ = value;
        }
    }
}
