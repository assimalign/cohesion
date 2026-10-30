using System;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Storage;
using Assimalign.Cohesion.Database.Storage.Tests.TestObjects;

namespace Assimalign.Cohesion.Database.Transactions.Tests.TestObjects;

// Deviates from the repo foreign-test public-surface rule per owner direction for #1726:
// temporary UnsafeAccessor bindings remain test-only until XT-4.
// Test-only accessors keep anonymous controls internal until XT-4 supplies the language
// surface. Linked model tests need no foreign InternalsVisibleTo or runtime inspection.
internal sealed class UndoMarkerDriver : TransactionRecordSpace
{
    [UnsafeAccessor(UnsafeAccessorKind.StaticMethod, Name = "CreateUndoMarkerAsync")]
    private static extern ValueTask<object> CreateMarker(TransactionRecordSpace? declaringType, TransactionCoordinator coordinator, TransactionContext context, CancellationToken cancellationToken);
    [UnsafeAccessor(UnsafeAccessorKind.StaticMethod, Name = "ReleaseUndoMarkerAsync")]
    private static extern ValueTask ReleaseMarker(TransactionRecordSpace? declaringType, TransactionCoordinator coordinator, TransactionContext context, object marker, CancellationToken cancellationToken);
    [UnsafeAccessor(UnsafeAccessorKind.StaticMethod, Name = "RollbackToUndoMarkerAsync")]
    private static extern ValueTask UndoMarker(TransactionRecordSpace? declaringType, TransactionCoordinator coordinator, TransactionContext context, object marker, CancellationToken cancellationToken);
    [UnsafeAccessor(UnsafeAccessorKind.StaticMethod, Name = "GetUndoFailureCode")]
    private static extern string? FailureCode(TransactionRecordSpace? declaringType, Exception exception);
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "set_UndoBatchApplied")]
    private static extern void ObserveBatch(RecordSpaceVersionStore store, Action? observer);

    internal static ValueTask<object> CreateAsync(TransactionCoordinator coordinator, TransactionContext context, CancellationToken cancellationToken = default)
        => CreateMarker(null, coordinator, context, cancellationToken);
    internal static ValueTask ReleaseAsync(TransactionCoordinator coordinator, TransactionContext context, object marker)
        => ReleaseMarker(null, coordinator, context, marker, default);
    internal static async ValueTask UndoAsync(TransactionCoordinator coordinator, TransactionContext context, object marker, CancellationToken cancellationToken = default)
    {
        ObserveBatch(coordinator.VersionStore, UndoReaderProbe.BatchObserver);
        try { await UndoMarker(null, coordinator, context, marker, cancellationToken); }
        finally { ObserveBatch(coordinator.VersionStore, null); }
    }
    internal static string? Code(Exception exception) => FailureCode(null, exception);
    internal void FinishStream(TransactionContext context) => EndWriteStream(context);
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
    private static readonly AsyncLocal<Action?> _batchObserver = new();
    internal static Action? BatchObserver => _batchObserver.Value;

    internal static async Task RunAsync(Func<Task> read, Func<Task> writeAndUndo)
    {
        using var stop = new CancellationTokenSource();
        using var started = new ManualResetEventSlim();
        using var progress = new ManualResetEventSlim();
        long reads = 0, overlaps = 0;
        int epoch = 0, rounds = 0;
        _batchObserver.Value = () =>
        {
            progress.Reset();
            Interlocked.Increment(ref epoch); // odd only inside actual physical undo
            try { progress.Wait(TimeSpan.FromSeconds(30)).ShouldBeTrue("a reader must complete inside actual undo"); }
            finally { Interlocked.Increment(ref epoch); }
        };
        var reader = DedicatedTestTask.Run(async () =>
        {
            started.Set();
            while (!stop.IsCancellationRequested)
            {
                int before = Volatile.Read(ref epoch);
                await read();
                Interlocked.Increment(ref reads);
                // A read must start and finish in the same actual undo interval. A read
                // blocked by undo cannot count merely because it samples a later flag.
                if ((before & 1) != 0 && Volatile.Read(ref epoch) == before)
                { Interlocked.Increment(ref overlaps); progress.Set(); }
            }
        });
        var elapsed = Stopwatch.StartNew();
        try
        {
            started.Wait(TimeSpan.FromSeconds(30)).ShouldBeTrue();
            do
            {
                await writeAndUndo();
                rounds++;
            }
            while ((elapsed.Elapsed < TimeSpan.FromMilliseconds(250) || rounds < 8 || Interlocked.Read(ref reads) < 16 || Interlocked.Read(ref overlaps) == 0)
                && elapsed.Elapsed < TimeSpan.FromSeconds(30));
        }
        finally
        {
            _batchObserver.Value = null;
            stop.Cancel();
            await reader.WaitAsync(TimeSpan.FromSeconds(30));
        }
        rounds.ShouldBeGreaterThanOrEqualTo(8);
        reads.ShouldBeGreaterThanOrEqualTo(16);
        overlaps.ShouldBeGreaterThan(0);
    }
}
