using System;

using Assimalign.Cohesion.Database.Sql.Internal;
using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Sql.Catalog;

public sealed partial class SqlCatalog
{
    private const int allocationReservationSize = 32;
    private bool _allocationClosed;

    // Disposal and allocation share the catalog monitor, including cached values.
    // Closing waits for an in-flight reservation before either storage can close.
    internal void CloseAllocation()
    {
        lock (_sync) { _allocationClosed = true; }
    }

    // Test-only observation/fault seams; no callback is installed by the engine.
    internal Action? AllocationEntering { get; set; }
    internal Action? ReservationCommitting { get; set; }
    internal Action? ReservationCommitted { get; set; }

    // Lock/latch order: caller's existing engine object grants (if any), catalog _sync,
    // catalog storage transaction/apply gate, then storage page latches. Allocation
    // never acquires engine/coordinator locks, waits for DML, or calls another catalog.
    // Create/replace/drop and allocation use the same monitor, preventing stale owners
    // from allocating after retirement. Storage commits finish before publishing cursors.
    private bool TryAllocate(SqlAllocationCounter counter, ulong id, int kind, long increment,
        long minimum, long maximum, bool cycle, out long value)
    {
        Int128 next = counter.Next;
        if (next < minimum || next > maximum)
        {
            if (!cycle) { value = default; return false; }
            // PostgreSQL sequence.c:735-784: wrap to the directional bound, not START.
            next = increment > 0 ? minimum : maximum;
        }
        if (counter.Remaining == 0)
        {
            Int128 distance = increment > 0 ? maximum - next : next - minimum;
            Int128 step = increment > 0 ? (Int128)increment : -(Int128)increment;
            int count = (int)Int128.Min(allocationReservationSize, distance / step + 1);
            long last = (long)(next + (Int128)(count - 1) * increment);
            var record = EncodeAllocation(kind, id, last, isCalled: true);
            using var transaction = _storage.BeginTransaction();
            var location = UpsertRecord(transaction, counter.Location, record);
            ReservationCommitting?.Invoke();
            transaction.Commit();
            counter.Location = location;
            counter.Remaining = count;
            counter.Next = next;
            if (counter is SqlSequenceCounter sequence)
            {
                sequence.Reserved = last;
                sequence.IsCalled = true;
            }
            ReservationCommitted?.Invoke();
        }
        value = (long)next;
        counter.Next = next + increment;
        counter.Remaining--;
        return true;
    }

    private static byte[] EncodeAllocation(int kind, ulong id, long reserved, bool isCalled)
    {
        var writer = new DatabaseKeyWriter().AppendInt32(kind).AppendInt64((long)id).AppendInt64(reserved);
        if (kind == sequenceCounterRecordKind) { writer.AppendBoolean(isCalled); }
        return writer.ToArray();
    }

    private static Int128 RecoverAllocation(long reserved, long increment, bool isCalled)
        => (Int128)reserved + (isCalled ? increment : 0);
}
