using System;
using System.Runtime.CompilerServices;

using Assimalign.Cohesion.Database.Storage;

namespace Assimalign.Cohesion.Database.Sql.Internal;

[System.Diagnostics.DebuggerDisplay("SQL hash spill metrics")]
[System.Diagnostics.DebuggerTypeProxy(typeof(SqlHashSpillMetrics.DebugView))]
internal sealed class SqlHashSpillMetrics
{
    private static readonly ConditionalWeakTable<SqlStatementMetrics, SqlHashSpillMetrics> _values = new();
    internal long PhysicalBytes { get; private set; }
    internal long PeakBytes { get; private set; }
    internal long Runs { get; private set; }
    internal long Batches { get; private set; }
    internal int Passes { get; private set; }
    internal int MergePasses { get; private set; }
    internal static SqlHashSpillMetrics? For(SqlStatementMetrics metrics) => _values.TryGetValue(metrics, out var value) ? value : null;
    internal static void Begin(SqlStatementMetrics metrics) => _values.GetValue(metrics, _ => new());
    internal static void Batch(SqlStatementMetrics metrics, int pass)
    { var value = For(metrics)!; value.Batches++; value.Passes = Math.Max(value.Passes, pass); }
    internal static void Sort(SqlStatementMetrics metrics, StorageTempStatement statement, StorageTempSorter sorter)
    {
        var value = For(metrics)!; value.Runs += sorter.RunsCreated; value.MergePasses += sorter.MergePasses;
        Refresh(metrics, statement);
    }
    internal static void Refresh(SqlStatementMetrics metrics, StorageTempStatement statement)
    { var value = For(metrics)!; value.PhysicalBytes = statement.PhysicalBytes; value.PeakBytes = statement.PeakBytes; }
    private sealed class DebugView { public DebugView(SqlHashSpillMetrics target) { } }
}
