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

    // Each entry describes one operator family. Recursive unions and hash joins reuse
    // unused window slots so ANALYZE retains its allocation size.
    internal long RecursiveIterations { get => WindowTransitions; set => WindowTransitions = value; }
    internal long RecursiveRows { get => WindowInverseTransitions; set => WindowInverseTransitions = value; }
    internal bool HashSpillAllowed { get => WindowTransitions != 0; set => WindowTransitions = value ? 1 : 0; }
    internal bool HashSpilled { get => WindowInverseTransitions != 0; set => WindowInverseTransitions = value ? 1 : 0; }

    private sealed class DebugView
    {
        public DebugView(global::Assimalign.Cohesion.Database.Sql.Internal.SqlOperatorWork value)
        {
            _ = value;
        }
    }
}
