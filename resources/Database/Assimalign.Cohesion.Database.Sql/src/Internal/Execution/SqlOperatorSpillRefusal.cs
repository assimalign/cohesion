using System;
using System.Runtime.CompilerServices;
using System.Threading;

using Assimalign.Cohesion.Database.Storage;

namespace Assimalign.Cohesion.Database.Sql.Internal;

// Only a newly admitted operator records here. Existing output/sort refusals keep
// their diagnostics when they precede that operator's original memory boundary.
internal static class SqlOperatorSpillRefusal
{
    private static ConditionalWeakTable<SqlStatementMetrics, SqlExecutionException>? _refusals;
    internal static void Record(SqlStatementMetrics metrics, SqlExecutionException refusal)
        => LazyInitializer.EnsureInitialized(ref _refusals).GetValue(metrics, _ => refusal);
    internal static void RecordOutputHandoff(SqlStatementMetrics metrics, SqlExecutionException refusal)
    {
        if (metrics.IsStageResult || metrics.IsSetOutput) { Record(metrics, refusal); }
    }
    internal static bool IsTemporary(SqlStatementMetrics metrics, Exception failure)
        => (For(metrics) is not null || !IsExistingRelationFailure(failure)) && (failure is StorageTempException || failure is SqlExecutionException sql
            && sql.Diagnostics[0].Code is { } code && code.StartsWith("CK05", StringComparison.Ordinal));
    internal static bool IsExistingRelationFailure(Exception failure)
    {
        for (Exception? cause = failure; cause is not null; cause = cause.InnerException)
        { if (cause.Data.Contains("SqlExistingRelationTemp")) { return true; } }
        return false;
    }
    internal static SqlExecutionException? For(SqlStatementMetrics metrics)
        => _refusals?.TryGetValue(metrics, out var refusal) == true ? refusal : null;
    internal static SqlExecutionException Preserve(SqlStatementMetrics metrics, Exception cause, long budget)
    {
        var refusal = For(metrics) ?? SqlExecutionException.StatementMemoryBudget(budget);
        refusal.Data[nameof(SqlOperatorSpillRefusal)] = cause; Record(metrics, refusal); return refusal;
    }
    internal static void AttachCleanup(SqlStatementMetrics metrics, Exception failure)
    {
        if (For(metrics) is not { } refusal || ReferenceEquals(refusal, failure)) { return; }
        foreach (string key in new[] { "InnerCleanup", "SetStageCleanup", "SetOperandCleanup" })
        { if (refusal.Data.Contains(key)) { failure.Data[key] = refusal.Data[key]; } }
    }
}
