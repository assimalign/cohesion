using System.Collections.Generic;
using System.Threading;

namespace Assimalign.Cohesion.Database.Sql.Internal;

internal sealed partial class SqlPlanExecutor
{
    // CTAS retains its specific kept-output admission policy. Its source still
    // pipelines, with existing hash/window/recursive/grouping-set grants unchanged.
    private bool _tableAsOutputAdmissionOnly;
    private long SelectBufferBudget => _isDml || _tableAsOutputAdmissionOnly || RecursiveState is not null ? 0 : _definitions.StatementMemoryBudget;

    // Plain operators own byte counts only. Existing ledgers remain exclusive.
    private bool ChargesSelectBuffers => !_isDml && !_tableAsOutputAdmissionOnly && RecursiveState is null && SqlWindowMemory.For(_metrics) is null;

    private void ReserveSelectMemory(ref long reserved, long bytes)
    {
        if (!ChargesSelectBuffers || _metrics is null) { return; }
        _metrics.ReserveHashMemory(bytes, _definitions.StatementMemoryBudget);
        reserved += bytes;
    }

    private void RetainSelectRow(ref long reserved, object?[] row)
    {
        if (!ChargesSelectBuffers || _metrics is null) { return; }
        ReserveSelectMemory(ref reserved, 32L + SqlHashJoinTable.RowBytes(row));
    }

    private void ReleaseSelectMemory(ref long reserved)
    {
        _metrics?.ReleaseHashMemory(reserved);
        reserved = 0;
    }

    private long AdmitSelectRows(IReadOnlyList<object?[]> rows, CancellationToken cancellationToken)
    {
        if (!ChargesSelectBuffers || _metrics is null) { return 0; }
        long reserved = 0;
        try
        {
            for (int i = 0; i < rows.Count; i++)
            { cancellationToken.ThrowIfCancellationRequested(); RetainSelectRow(ref reserved, rows[i]); }
            return reserved;
        }
        catch { ReleaseSelectMemory(ref reserved); throw; }
    }

    private long AdmitSelectResult(object result, CancellationToken cancellationToken)
        => ChargesSelectBuffers && result is SqlMaterializedResultSet rows && _metrics is not null
            ? rows.ReserveMemory(_metrics, _definitions.StatementMemoryBudget, cancellationToken) : 0;

    private void ReleaseSelectRows(ref long reserved, List<object?[]> rows)
    {
        if (reserved == 0) { return; }
        foreach (var row in rows)
        {
            long bytes = 32L + SqlHashJoinTable.RowBytes(row);
            _metrics!.ReleaseHashMemory(bytes);
            reserved -= bytes;
        }
    }
}
