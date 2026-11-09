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
    internal StorageTempStatement? JoinStatement { get; private set; }
    internal bool FullJoin { get; private set; }
    internal Exception? JoinCleanupFailure { get; private set; }
    private long _joinPhysicalBytes;
    internal static void JoinCleanup(SqlStatementMetrics metrics, Exception failure)
    { Begin(metrics); For(metrics)!.JoinCleanupFailure ??= failure; }
    internal static SqlHashSpillMetrics? For(SqlStatementMetrics metrics) => _values.TryGetValue(metrics, out var value) ? value : null;
    internal static void Begin(SqlStatementMetrics metrics) => _values.GetValue(metrics, _ => new());
    internal static StorageTempStatement JoinTemp(SqlStatementMetrics metrics, SqlDatabaseSession owner, bool full = false)
    {
        Begin(metrics);
        var value = For(metrics)!;
        value.FullJoin |= full;
        return value.JoinStatement ??= owner.Database.Engine.TempSpace!.CreateStatement(owner.Database.Engine.StatementTempSpaceQuota);
    }
    internal static StorageTempStatement? DetachJoinTemp(SqlStatementMetrics metrics)
    {
        var value = For(metrics);
        if (value is null) { return null; }
        var statement = value.JoinStatement; value.JoinStatement = null; return statement;
    }
    internal static void Batch(SqlStatementMetrics metrics, int pass)
    { var value = For(metrics)!; value.Batches++; value.Passes = Math.Max(value.Passes, pass); }
    internal static void Sort(SqlStatementMetrics metrics, StorageTempStatement statement, StorageTempSorter sorter)
    {
        var value = For(metrics)!; value.Runs += sorter.RunsCreated; value.MergePasses += sorter.MergePasses;
        Refresh(metrics, statement);
    }
    internal static void Refresh(SqlStatementMetrics metrics, StorageTempStatement statement)
    { var value = For(metrics)!; value.PhysicalBytes = statement.PhysicalBytes; value.PeakBytes = statement.PeakBytes; }
    internal static void RefreshJoin(SqlStatementMetrics metrics, StorageTempStatement statement)
    {
        var value = For(metrics)!;
        value._joinPhysicalBytes = Math.Max(value._joinPhysicalBytes, statement.PhysicalBytes);
        value.PhysicalBytes = Math.Max(value.PhysicalBytes, value._joinPhysicalBytes);
        value.PeakBytes = statement.PeakBytes;
    }
    private sealed class DebugView { public DebugView(SqlHashSpillMetrics target) { } }
}
