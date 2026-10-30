using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Storage;
using Assimalign.Cohesion.Database.Storage.Tests.TestObjects;

namespace Assimalign.Cohesion.Database.Transactions.Tests.TestObjects;

// Test-only access to the same protected composition seam model adapters use.
internal sealed class UndoMarkerDriver : TransactionRecordSpace
{
    internal static ValueTask<object> CreateAsync(TransactionCoordinator coordinator, TransactionContext context, CancellationToken cancellationToken = default)
        => CreateUndoMarkerAsync(coordinator, context, cancellationToken);
    internal static ValueTask ReleaseAsync(TransactionCoordinator coordinator, TransactionContext context, object marker)
        => ReleaseUndoMarkerAsync(coordinator, context, marker);
    internal static ValueTask UndoAsync(TransactionCoordinator coordinator, TransactionContext context, object marker, CancellationToken cancellationToken = default)
        => RollbackToUndoMarkerAsync(coordinator, context, marker, cancellationToken);
    internal static string? Code(Exception exception) => GetUndoFailureCode(exception);
    protected override ReadOnlyMemory<byte> ReadCore(PageId pageId, int slotIndex) => throw new NotSupportedException();
    protected override void UpdateCore(StorageTransaction transaction, PageId pageId, int slotIndex, ReadOnlySpan<byte> record) => throw new NotSupportedException();
    protected override void DeleteCore(StorageTransaction transaction, PageId pageId, int slotIndex) => throw new NotSupportedException();
    protected override ulong PackLocationCore(PageId pageId, int slotIndex) => throw new NotSupportedException();
    protected override (PageId PageId, int SlotIndex) UnpackLocationCore(ulong location) => throw new NotSupportedException();
}

[CollectionDefinition("Savepoint engine races", DisableParallelization = true)]
public sealed class SavepointConcurrencyCollection { }

internal static class UndoReaderProbe
{
    internal static async Task RunAsync(Func<Task> read, Func<Task> writeAndUndo)
    {
        using var stop = new CancellationTokenSource();
        using var started = new ManualResetEventSlim();
        long reads = 0, overlaps = 0;
        int undoing = 0, rounds = 0;
        var reader = DedicatedTestTask.Run(async () =>
        {
            started.Set();
            while (!stop.IsCancellationRequested)
            {
                await read();
                Interlocked.Increment(ref reads);
                if (Volatile.Read(ref undoing) != 0) { Interlocked.Increment(ref overlaps); }
            }
        });
        var elapsed = Stopwatch.StartNew();
        try
        {
            started.Wait(TimeSpan.FromSeconds(30)).ShouldBeTrue();
            do
            {
                Volatile.Write(ref undoing, 1);
                try { await writeAndUndo(); rounds++; }
                finally { Volatile.Write(ref undoing, 0); }
            }
            while ((elapsed.Elapsed < TimeSpan.FromMilliseconds(250) || rounds < 8 || Interlocked.Read(ref reads) < 16 || Interlocked.Read(ref overlaps) == 0)
                && elapsed.Elapsed < TimeSpan.FromSeconds(30));
        }
        finally
        {
            stop.Cancel();
            await reader.WaitAsync(TimeSpan.FromSeconds(30));
        }
        rounds.ShouldBeGreaterThanOrEqualTo(8);
        reads.ShouldBeGreaterThanOrEqualTo(16);
        overlaps.ShouldBeGreaterThan(0);
    }
}
