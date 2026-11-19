using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;

using Assimalign.Cohesion.Database.Storage;

namespace Assimalign.Cohesion.Database.Sql.Internal;

// Per-group ANALYZE state is created only after a spill. Ordinary/fitting stage
// layouts and non-analyzed execution allocate no observation map.
[System.Diagnostics.DebuggerDisplay("Window spill analysis")]
[System.Diagnostics.DebuggerTypeProxy(typeof(DebugView))]
internal sealed class SqlWindowSpillMetrics
{
    private static ConditionalWeakTable<SqlStatementMetrics, Dictionary<SqlWindowGroup, SqlWindowSpillMetrics>>? _values;
    private long _bytes;
    private long _peak;
    private long _runs;
    private int _passes;

    internal static void Record(SqlStatementMetrics metrics, SqlWindowGroup group, StorageTempStatement statement, StorageTempSorter sorter)
    {
        if (!metrics.IsAnalyzing) { return; }
        var groups = LazyInitializer.EnsureInitialized(ref _values).GetValue(metrics, _ => new(ReferenceEqualityComparer.Instance));
        if (!groups.TryGetValue(group, out var value)) { groups.Add(group, value = new()); }
        value._bytes = Math.Max(value._bytes, statement.PhysicalBytes); value._peak = statement.PeakBytes;
        value._runs += sorter.RunsCreated; value._passes += sorter.MergePasses;
    }

    internal static string Detail(SqlStatementMetrics? metrics, SqlWindowGroup group)
        => metrics is not null && _values?.TryGetValue(metrics, out var groups) == true && groups.TryGetValue(group, out var value)
            ? $"; spilled_bytes={value._bytes}; spill_peak_bytes={value._peak}; spill_runs={value._runs}; spill_merge_passes={value._passes}" : "";

    private sealed class DebugView { public DebugView(SqlWindowSpillMetrics value) { } }
}
