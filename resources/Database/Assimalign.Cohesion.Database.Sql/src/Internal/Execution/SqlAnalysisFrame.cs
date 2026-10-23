namespace Assimalign.Cohesion.Database.Sql.Internal;

/// <summary>One analysis invocation; nested plans retain separate physical-work counts.</summary>
[global::System.Diagnostics.DebuggerDisplay("SqlAnalysisFrame")]
[global::System.Diagnostics.DebuggerTypeProxy(typeof(global::Assimalign.Cohesion.Database.Sql.Internal.SqlAnalysisFrame.DebugView))]
internal sealed class SqlAnalysisFrame
{
    internal SqlAnalysisFrame(SqlAnalysisFrame? parent, long records, long pairs)
    {
        Parent = parent;
        StartRecords = records;
        StartPairs = pairs;
    }

    internal SqlAnalysisFrame? Parent { get; }
    internal long StartRecords { get; }
    internal long StartPairs { get; }
    internal long ChildRecords;
    internal long ChildPairs;

    private sealed class DebugView
    {
        public DebugView(global::Assimalign.Cohesion.Database.Sql.Internal.SqlAnalysisFrame value)
        {
            _ = value;
        }
    }
}
