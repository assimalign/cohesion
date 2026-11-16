using System.Collections.Generic;
using System.Threading;

namespace Assimalign.Cohesion.Database.Sql.Internal;

internal sealed partial class SqlPlanExecutor
{
    private long SelectBufferBudget => _isDml || RecursiveState is not null ? 0 : _definitions.StatementMemoryBudget;

    // Plain buffers own their bytes and wide-cell identities. Existing ledgers remain exclusive.
    private bool ChargesSelectBuffers => ChargesAdditionalSelectBuffers && SqlWindowMemory.For(_metrics) is null;

    // These buffers are absent from the window ledger.
    private bool ChargesAdditionalSelectBuffers => !_isDml && RecursiveState is null;

    private void ReserveSelectMemory(ref long reserved, long bytes, bool includeWindow = false)
    {
        if (!(includeWindow ? ChargesAdditionalSelectBuffers : ChargesSelectBuffers) || _metrics is null) { return; }
        _metrics.ReservePlainSelectMemory(bytes, _definitions.StatementMemoryBudget);
        reserved += bytes;
    }

    private void RetainSelectRow(ref SqlSelectMemory reserved, object?[] row, bool includeWindow = false, long overhead = 32)
    {
        if (!(includeWindow ? ChargesAdditionalSelectBuffers : ChargesSelectBuffers) || _metrics is null) { return; }
        long bytes = reserved.RowBytes(row, out bool wide, overhead);
        _metrics.ReservePlainSelectMemory(bytes, _definitions.StatementMemoryBudget);
        reserved.Bytes += bytes;
        if (wide) { reserved.AddRow(row); }
    }

    private void RetainSelectValue(ref SqlSelectMemory reserved, object? value, long overhead = 0)
    {
        if (!ChargesSelectBuffers || _metrics is null) { return; }
        long bytes = overhead + reserved.ValueBytes(value, out bool wide);
        _metrics.ReservePlainSelectMemory(bytes, _definitions.StatementMemoryBudget);
        reserved.Bytes += bytes;
        if (wide) { reserved.AddValue(value!); }
    }

    private void ReleaseSelectMemory(ref long reserved)
    {
        _metrics?.ReleasePlainSelectMemory(reserved);
        reserved = 0;
    }

    private void ReserveSelectMemory(ref SqlSelectMemory reserved, long bytes, bool includeWindow = false)
        => ReserveSelectMemory(ref reserved.Bytes, bytes, includeWindow);

    private void ReleaseSelectMemory(ref SqlSelectMemory reserved)
    {
        _metrics?.ReleasePlainSelectMemory(reserved.Bytes);
        reserved.Clear();
    }

    private long AdmitSelectRows(IReadOnlyList<object?[]> rows, CancellationToken cancellationToken, bool includeWindow = false)
    {
        if (!(includeWindow ? ChargesAdditionalSelectBuffers : ChargesSelectBuffers) || _metrics is null) { return 0; }
        SqlSelectMemory reserved = default;
        try
        {
            for (int i = 0; i < rows.Count; i++)
            { cancellationToken.ThrowIfCancellationRequested(); RetainSelectRow(ref reserved, rows[i], includeWindow); }
            return reserved.Bytes;
        }
        catch { ReleaseSelectMemory(ref reserved); throw; }
    }

    private long AdmitSelectResult(object result, CancellationToken cancellationToken)
        => ChargesSelectBuffers && result is SqlMaterializedResultSet rows && _metrics is not null
            ? rows.ReserveMemory(_metrics, _definitions.StatementMemoryBudget, cancellationToken, plainSelect: true) : 0;

    private void ReleaseSelectRows(ref SqlSelectMemory reserved, List<object?[]> rows)
    {
        if (reserved.Bytes == 0) { return; }
        foreach (var row in rows)
        {
            long bytes = reserved.RemoveRow(row);
            _metrics!.ReleasePlainSelectMemory(bytes);
            reserved.Bytes -= bytes;
        }
    }
}
