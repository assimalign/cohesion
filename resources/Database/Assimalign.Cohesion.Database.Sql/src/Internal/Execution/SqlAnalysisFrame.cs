namespace Assimalign.Cohesion.Database.Sql.Internal;

/// <summary>One analysis invocation; nested plans retain separate physical-work counts.</summary>
internal sealed class SqlAnalysisFrame(SqlAnalysisFrame? parent, long records, long pairs)
{
    internal SqlAnalysisFrame? Parent { get; } = parent;
    internal long StartRecords { get; } = records;
    internal long StartPairs { get; } = pairs;
    internal long ChildRecords;
    internal long ChildPairs;
}
