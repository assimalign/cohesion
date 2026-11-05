using System.Collections.Generic;
using System.Threading;

namespace Assimalign.Cohesion.Database.Sql.Internal;

internal sealed partial class SqlPlanExecutor
{
    private long SelectBufferBudget => _isDml || RecursiveState is not null ? 0 : _definitions.StatementMemoryBudget;

    // Plain operators own byte counts only. Existing ledgers remain exclusive.
    private bool ChargesSelectBuffers => ChargesAdditionalSelectBuffers && SqlWindowMemory.For(_metrics) is null;

    // These buffers are absent from the window ledger.
    private bool ChargesAdditionalSelectBuffers => !_isDml && RecursiveState is null;

    private void ReserveSelectMemory(ref long reserved, long bytes, bool includeWindow = false)
    {
        if (!(includeWindow ? ChargesAdditionalSelectBuffers : ChargesSelectBuffers) || _metrics is null) { return; }
        _metrics.ReservePlainSelectMemory(bytes, _definitions.StatementMemoryBudget);
        reserved += bytes;
    }

    private void RetainSelectRow(ref long reserved, object?[] row, bool includeWindow = false)
    {
        if (!(includeWindow ? ChargesAdditionalSelectBuffers : ChargesSelectBuffers) || _metrics is null) { return; }
        long bytes = 32L + SqlHashJoinTable.RowBytes(row);
        _metrics.ReservePlainSelectMemory(bytes, _definitions.StatementMemoryBudget);
        reserved += bytes;
    }

    private void ReleaseSelectMemory(ref long reserved)
    {
        _metrics?.ReleasePlainSelectMemory(reserved);
        reserved = 0;
    }

    private long AdmitSelectRows(IReadOnlyList<object?[]> rows, CancellationToken cancellationToken, bool includeWindow = false)
    {
        if (!(includeWindow ? ChargesAdditionalSelectBuffers : ChargesSelectBuffers) || _metrics is null) { return 0; }
        long reserved = 0;
        try
        {
            for (int i = 0; i < rows.Count; i++)
            { cancellationToken.ThrowIfCancellationRequested(); RetainSelectRow(ref reserved, rows[i], includeWindow); }
            return reserved;
        }
        catch { ReleaseSelectMemory(ref reserved); throw; }
    }

    private long AdmitSelectResult(object result, CancellationToken cancellationToken)
        => ChargesSelectBuffers && result is SqlMaterializedResultSet rows && _metrics is not null
            ? rows.ReserveMemory(_metrics, _definitions.StatementMemoryBudget, cancellationToken, plainSelect: true) : 0;

    private void ReleaseSelectRows(ref long reserved, List<object?[]> rows)
    {
        if (reserved == 0) { return; }
        foreach (var row in rows)
        {
            long bytes = 32L + SqlHashJoinTable.RowBytes(row);
            _metrics!.ReleasePlainSelectMemory(bytes);
            reserved -= bytes;
        }
    }
}
