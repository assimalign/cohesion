using System.Runtime.CompilerServices;

using Assimalign.Cohesion.Database.Storage;

namespace Assimalign.Cohesion.Database.Sql.Internal;

[System.Diagnostics.DebuggerDisplay("SQL spill metrics")]
[System.Diagnostics.DebuggerTypeProxy(typeof(SqlSortSpillMetrics.DebugView))]
internal sealed class SqlSortSpillMetrics
{
    private static readonly ConditionalWeakTable<SqlStatementMetrics, SqlSortSpillMetrics> _values = new();
    internal long PhysicalBytes { get; private set; }
    internal long PeakBytes { get; private set; }
    internal long Runs { get; private set; }
    internal int MergePasses { get; private set; }
    internal static SqlSortSpillMetrics? For(SqlStatementMetrics metrics) => _values.TryGetValue(metrics, out var value) ? value : null;
    internal static void Record(SqlStatementMetrics metrics, StorageTempStatement statement, StorageTempSorter sorter)
    {
        var value = _values.GetValue(metrics, _ => new());
        value.PhysicalBytes = statement.PhysicalBytes; value.PeakBytes = statement.PeakBytes;
        value.Runs = sorter.RunsCreated; value.MergePasses = sorter.MergePasses;
    }
    internal static void Refresh(SqlStatementMetrics metrics, StorageTempStatement statement)
    {
        if (For(metrics) is { } value) { value.PhysicalBytes = statement.PhysicalBytes; value.PeakBytes = statement.PeakBytes; }
    }
    internal static void RecordOutput(SqlStatementMetrics metrics, StorageTempStatement statement)
    {
        var value = _values.GetValue(metrics, _ => new());
        value.PhysicalBytes = statement.PhysicalBytes; value.PeakBytes = statement.PeakBytes; value.Runs = 1;
    }
    private sealed class DebugView { public DebugView(SqlSortSpillMetrics target) { } }
}
