using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;
using Xunit.Abstractions;

using Assimalign.Cohesion.Database.Storage;
using Assimalign.Cohesion.Database.Storage.Tests.TestObjects;
using Assimalign.Cohesion.Database.Transactions.Internal;
using Assimalign.Cohesion.Database.Transactions.Tests.TestObjects;

namespace Assimalign.Cohesion.Database.Transactions.Tests;

[Collection("Undo substrate")]
public sealed class TransactionUndoMarkerFixTests
{
    private static readonly TimeSpan _timeout = TimeSpan.FromSeconds(30);
    private readonly ITestOutputHelper _output;

    public TransactionUndoMarkerFixTests(ITestOutputHelper output) { _output = output; }

    [Fact(DisplayName = "Cohesion Test [Transactions] - Reader overlap: a mutex serializing reads against actual undo must fail the probe")]
    public async Task UndoReaderProbe_SerializedReads_ShouldRejectFalseOverlap()
    {
        // Arrange: readers can progress during statement setup, but never inside undo.
        using var storage = new UndoTestStorage();
        await using var coordinator = new TransactionCoordinator(storage, storage.Log, storage.Records);
        var writer = await coordinator.BeginAsync(IsolationLevel.Snapshot);
        var marker = await UndoMarkerDriver.CreateAsync(coordinator, writer);
        var mutex = new object();
        using var readDuringSetup = new AutoResetEvent(false);

        // Act / Assert
        var failure = await Record.ExceptionAsync(() => UndoReaderProbe.RunAsync(() =>
        {
            lock (mutex) { readDuringSetup.Set(); }
            return Task.CompletedTask;
        }, async () =>
        {
            await InsertAsync(coordinator, storage, writer, 1);
            readDuringSetup.WaitOne(_timeout).ShouldBeTrue();
            lock (mutex)
            { UndoMarkerDriver.UndoAsync(coordinator, writer, marker).GetAwaiter().GetResult(); }
        }));
        failure.ShouldBeOfType<ShouldAssertException>();
        await coordinator.RollbackAsync(writer);
    }

    [Fact(DisplayName = "Cohesion Test [Transactions] - Created undo: stamp validation supplies the record without a second adapter copy")]
    public async Task RollbackToMarker_CreatedRecords_ShouldAvoidExtraCopies()
    {
        // Arrange
        using var storage = new UndoTestStorage();
        await using var coordinator = new TransactionCoordinator(storage, storage.Log, storage.Records);
        var writer = await coordinator.BeginAsync(IsolationLevel.Snapshot);
        var marker = await coordinator.CreateUndoMarkerAsync(writer);
        await coordinator.ApplyStatementAsync(writer, bracket =>
        {
            var record = new byte[7_000];
            RecordVersionStamp.WriteWriter(record, writer.Sequence);
            for (int i = 0; i < 64; i++)
            {
                var location = storage.Insert(bracket, record);
                coordinator.VersionStore.RecordCreated(writer.Sequence, location.PageId, location.SlotIndex);
            }
            return 0;
        });
        storage.AdapterReads = 0;

        // Act
        long before = GC.GetAllocatedBytesForCurrentThread();
        var undo = coordinator.RollbackToUndoMarkerAsync(writer, marker);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        undo.IsCompletedSuccessfully.ShouldBeTrue();
        await undo;

        // Assert
        _output.WriteLine($"64 created 7,000-byte records: {allocated} undo bytes; {storage.AdapterReads} extra adapter reads.");
        storage.AdapterReads.ShouldBe(0);
        await coordinator.RollbackAsync(writer);
    }

    [Theory(DisplayName = "Cohesion Test [Transactions] - Statement compensation: failed memory repair fences commit and writes until undo repairs it")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ApplyStatement_FailedCompensation_ShouldFenceUntilUndo(bool wholeRollback)
    {
        // Arrange
        using var storage = new UndoTestStorage();
        await using var coordinator = new TransactionCoordinator(storage, storage.Log, storage.Records);
        var writer = await coordinator.BeginAsync(IsolationLevel.Snapshot);
        var marker = await coordinator.CreateUndoMarkerAsync(writer);
        int value = 0;
        var repair = new MemoryRepair(() => value = 0) { Fail = true };
        var statementFailure = new InvalidOperationException("Injected statement failure");

        // Act: the physical bracket is rolled back before compensation throws.
        var failure = await Should.ThrowAsync<TransactionUndoException>(() => coordinator.ApplyStatementAsync<int>(writer, (Func<StorageTransaction, int>)(_ =>
        {
            coordinator.VersionStore.RecordIndexEntryCreated(writer.Sequence, repair, ReadOnlyMemory<byte>.Empty, 0);
            value = 7;
            throw statementFailure;
        })).AsTask());

        // Assert
        AssertCompensationFailure(failure, statementFailure, repair.Failure);
        value.ShouldBe(7);
        coordinator.VersionStore.TrackedVersionCount.ShouldBe(1);
        (await Should.ThrowAsync<TransactionUndoException>(() => coordinator.CommitAsync(writer).AsTask())).Code.ShouldBe("CK04009");
        (await Should.ThrowAsync<TransactionUndoException>(() => coordinator.ApplyStatementAsync(writer, _ => 0).AsTask())).Code.ShouldBe("CK04009");
        repair.Fail = false;
        if (wholeRollback) { await coordinator.RollbackAsync(writer); }
        else
        {
            await coordinator.RollbackToUndoMarkerAsync(writer, marker);
            await coordinator.CommitAsync(writer);
        }
        value.ShouldBe(0);
        coordinator.VersionStore.TrackedVersionCount.ShouldBe(0);
    }

    [Theory(DisplayName = "Cohesion Test [Transactions] - Statement compensation: a draining commit must fence and retain an owner through disposal")]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task Commit_FailedCompensationDuringDrain_ShouldFenceAndReturnEndClaim(bool dispose, bool replace)
    {
        // Arrange
        using var storage = new UndoTestStorage();
        await using var coordinator = new TransactionCoordinator(storage, storage.Log, storage.Records);
        var writer = await coordinator.BeginAsync(IsolationLevel.Snapshot);
        if (replace) { writer = coordinator.ReplaceIsolationLevel(writer, IsolationLevel.Snapshot); }
        var marker = await coordinator.CreateUndoMarkerAsync(writer);
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int value = 0;
        var repair = new MemoryRepair(() => value = 0) { Fail = true };
        var statementFailure = new InvalidOperationException("Injected statement failure");
        var statement = coordinator.ApplyStatementAsync<int>(writer, async _ =>
        {
            coordinator.VersionStore.RecordIndexEntryCreated(writer.Sequence, repair, ReadOnlyMemory<byte>.Empty, 0);
            value = 7;
            await finish.Task;
            throw statementFailure;
        }).AsTask();
        var commit = coordinator.CommitAsync(writer).AsTask();
        commit.IsCompleted.ShouldBeFalse();
        writer.Current.ShouldBeSameAs(writer, "a draining end must retain exact replacement identity");
        Task? disposal = null;
        int drains = 0;
        coordinator.Manager.FailedCompensationDrained = () =>
        {
            drains++;
            if (dispose)
            {
                disposal = coordinator.DisposeAsync().AsTask();
                disposal.IsCompleted.ShouldBeFalse();
            }
        };

        // Act
        finish.TrySetResult();
        var failure = await Should.ThrowAsync<TransactionUndoException>(() => statement);
        AssertCompensationFailure(failure, statementFailure, repair.Failure);

        // Assert: the returned end claim admits repair by rollback-to.
        if (dispose) { await Should.ThrowAsync<TransactionAbortedException>(() => commit); }
        else { (await Should.ThrowAsync<TransactionUndoException>(() => commit)).Code.ShouldBe("CK04009"); }
        drains.ShouldBe(1);
        if (disposal is not null)
        {
            await disposal.WaitAsync(_timeout);
            writer.State.ShouldBe(TransactionState.Faulted);
            value.ShouldBe(0);
            coordinator.VersionStore.TrackedVersionCount.ShouldBe(0);
            return;
        }
        writer.State.ShouldBe(TransactionState.Active);
        writer.Current.ShouldBeSameAs(writer, "returning a failed-compensation end claim must restore replacement identity");
        value.ShouldBe(7);
        repair.Fail = false;
        await coordinator.RollbackToUndoMarkerAsync(writer, marker);
        value.ShouldBe(0);
        await coordinator.CommitAsync(writer);
    }

    [Fact(DisplayName = "Cohesion Test [Transactions] - Commit: disposal winning a failed-compensation drain reports an aborted transaction")]
    public async Task Commit_DisposalWinsFailedCompensationDrain_ShouldReportAborted()
    {
        // Arrange: hold an admitted apply so commit claims the end before the fence appears.
        using var storage = new UndoTestStorage();
        await using var coordinator = new TransactionCoordinator(storage, storage.Log, storage.Records);
        var writer = await coordinator.BeginAsync(IsolationLevel.Snapshot);
        await coordinator.LockManager.AcquireAsync(writer.Sequence, LockResource.Entry(1, 1), LockMode.Exclusive);
        var owner = coordinator.Manager.EnterApply(writer);
        Task? disposal = null;
        int drains = 0;
        coordinator.Manager.FailedCompensationDrained = () =>
        {
            drains++;
            disposal = coordinator.DisposeAsync().AsTask();
            disposal.IsCompleted.ShouldBeFalse();
        };
        var commit = coordinator.CommitAsync(writer).AsTask();
        commit.IsCompleted.ShouldBeFalse();

        // Act: the same fence and exit ordering used by failed statement compensation.
        owner.MarkPartialUndoPending();
        owner.ExitApply();
        await Should.ThrowAsync<TransactionAbortedException>(() => commit);
        await disposal.ShouldNotBeNull().WaitAsync(_timeout);

        // Assert: disposal won, so retrying partial undo is no longer possible.
        drains.ShouldBe(1);
        writer.State.ShouldBe(TransactionState.Faulted);
        coordinator.LockManager.HeldModes(writer.Sequence, LockResource.Entry(1, 1)).ShouldBe((byte)0);
    }

    private static void AssertCompensationFailure(TransactionUndoException failure, Exception statement, Exception repair)
    {
        failure.Code.ShouldBe("CK04009");
        var causes = failure.InnerException.ShouldBeOfType<AggregateException>().InnerExceptions;
        causes.Count.ShouldBe(2);
        causes[0].ShouldBeSameAs(statement);
        causes[1].ShouldBeSameAs(repair);
    }

    private sealed class MemoryRepair : RecordVersionIndex
    {
        private readonly Action _restore;
        internal MemoryRepair(Action restore) { _restore = restore; }
        internal bool Fail { get; set; }
        internal IOException Failure { get; } = new("Injected compensation failure");
        protected internal override void OnStatementRolledBackCore()
        {
            if (Fail) { throw Failure; }
            _restore();
        }
        protected override ValueTask EraseCoreAsync(StorageTransaction transaction, ReadOnlyMemory<byte> key, ulong entryReference, TransactionSequence writer, CancellationToken cancellationToken)
        { _restore(); return default; }
        protected override ValueTask ClearDeleterCoreAsync(StorageTransaction transaction, ReadOnlyMemory<byte> key, ulong entryReference, TransactionSequence writer, CancellationToken cancellationToken) => default;
    }

    [Theory(DisplayName = "Cohesion Test [Transactions] - Undo: empty and create-only suffixes visit no prefix tombstones and allocate no lookup")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RollbackToMarker_TombstoneFreeSuffix_ShouldSkipLargePrefix(bool create)
    {
        // Arrange: the prefix has 100,000 distinct tombstones; only the suffix is physical.
        using var storage = new UndoTestStorage();
        await using var coordinator = new TransactionCoordinator(storage, storage.Log, storage.Records);
        var writer = await coordinator.BeginAsync(IsolationLevel.Snapshot);
        var index = new CountingUndoIndex();
        await RecordPrefixAsync(coordinator, writer, index);
        var marker = await coordinator.CreateUndoMarkerAsync(writer);
        // Warm the empty control path before measuring its allocations.
        var warm = await coordinator.BeginAsync(IsolationLevel.Snapshot);
        var warmMarker = await coordinator.CreateUndoMarkerAsync(warm);
        await coordinator.RollbackToUndoMarkerAsync(warm, warmMarker);
        long emptyAllocated = MeasureUndo(coordinator, warm, warmMarker);
        if (create) { await InsertAsync(coordinator, storage, writer, 7); }
        index.HashCalls = 0;

        // Act: synchronous completion makes the thread allocation counter exact.
        long before = GC.GetAllocatedBytesForCurrentThread();
        var undo = coordinator.RollbackToUndoMarkerAsync(writer, marker);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        undo.IsCompletedSuccessfully.ShouldBeTrue();
        await undo;

        // Assert: no prefix visit can hash its index; the empty path has no lookup allocation.
        _output.WriteLine($"Prefix hashes: {index.HashCalls}; allocated: {allocated}; empty control: {emptyAllocated} bytes.");
        index.HashCalls.ShouldBe(0);
        if (!create) { allocated.ShouldBe(emptyAllocated); }
        coordinator.VersionStore.TrackedVersionCount.ShouldBe(100_000);
        await coordinator.CommitAsync(writer);
        await coordinator.CommitAsync(warm);
    }

    [Fact(DisplayName = "Cohesion Test [Transactions] - Undo: duplicate lookup allocation is bounded by suffix tombstones")]
    public async Task RollbackToMarker_TombstoneSuffix_ShouldBoundLookupAndKeepEarlierDelete()
    {
        // Arrange
        using var storage = new UndoTestStorage();
        await using var coordinator = new TransactionCoordinator(storage, storage.Log, storage.Records);
        var writer = await coordinator.BeginAsync(IsolationLevel.Snapshot);
        var index = new CountingUndoIndex();
        await RecordPrefixAsync(coordinator, writer, index);
        var marker = await coordinator.CreateUndoMarkerAsync(writer);
        await coordinator.ApplyStatementAsync(writer, _ =>
        {
            coordinator.VersionStore.RecordIndexEntryTombstoned(writer.Sequence, index, BitConverter.GetBytes(0), 1);
            coordinator.VersionStore.RecordIndexEntryTombstoned(writer.Sequence, index, BitConverter.GetBytes(100_000), 100_001);
            return 0;
        });

        // Act
        long before = GC.GetAllocatedBytesForCurrentThread();
        var undo = coordinator.RollbackToUndoMarkerAsync(writer, marker);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        undo.IsCompletedSuccessfully.ShouldBeTrue();
        await undo;

        // Assert: generous allowance for the ordinary physical bracket, independent of prefix size.
        _output.WriteLine($"Two suffix tombstones after 100,000 prefix entries: {allocated} allocated bytes.");
        allocated.ShouldBeLessThan(32_768);
        index.Clears.ShouldBe(1);
        coordinator.VersionStore.TrackedVersionCount.ShouldBe(100_000);
        await coordinator.CommitAsync(writer);
    }

    [Fact(DisplayName = "Cohesion Test [Transactions] - Undo: fenced statements report CK04009 while the writer remains active")]
    public async Task ApplyStatement_PendingUndo_ShouldReportRetryableUndoFailure()
    {
        // Arrange
        using var storage = new UndoTestStorage();
        await using var coordinator = new TransactionCoordinator(storage, storage.Log, storage.Records);
        var writer = await coordinator.BeginAsync(IsolationLevel.Snapshot);
        var marker = await coordinator.CreateUndoMarkerAsync(writer);
        var index = new UndoTestStorage.Index(storage) { BeforeUndo = () => throw new IOException("Injected undo failure") };
        await InsertAsync(coordinator, storage, writer, 1, index);
        await Should.ThrowAsync<IOException>(() => coordinator.RollbackToUndoMarkerAsync(writer, marker).AsTask());
        index.BeforeUndo = null;

        // Act / Assert
        var failure = await Should.ThrowAsync<TransactionUndoException>(() => InsertAsync(coordinator, storage, writer, 2).AsTask());
        failure.Code.ShouldBe("CK04009");
        writer.State.ShouldBe(TransactionState.Active);
        await coordinator.RollbackToUndoMarkerAsync(writer, marker);
        await coordinator.CommitAsync(writer);
    }

    [Theory(DisplayName = "Cohesion Test [Transactions] - Undo: failed inner undo can recover to an outer or equal-position earlier marker and commit the oracle")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RollbackToMarker_FailedInnerUndo_ShouldAdmitOuterAndCommitOracle(bool samePosition)
    {
        // Arrange: the oracle contains only the write before the outer marker.
        using var storage = new UndoTestStorage();
        await using var coordinator = new TransactionCoordinator(storage, storage.Log, storage.Records);
        var writer = await coordinator.BeginAsync(IsolationLevel.Snapshot);
        await InsertAsync(coordinator, storage, writer, 1);
        var outer = await coordinator.CreateUndoMarkerAsync(writer);
        if (!samePosition) { await InsertAsync(coordinator, storage, writer, 2); }
        var inner = await coordinator.CreateUndoMarkerAsync(writer);
        var index = new UndoTestStorage.Index(storage) { BeforeUndo = () => throw new IOException("Injected second-batch failure") };
        await InsertAsync(coordinator, storage, writer, 3, index);
        for (int i = 0; i < 70; i++) { await InsertAsync(coordinator, storage, writer, 4); }
        await Should.ThrowAsync<IOException>(() => coordinator.RollbackToUndoMarkerAsync(writer, inner).AsTask());
        writer.PartialUndoPending.ShouldBeTrue();
        index.BeforeUndo = null;

        // Act
        await coordinator.RollbackToUndoMarkerAsync(writer, outer);
        writer.PartialUndoPending.ShouldBeFalse();
        await InsertAsync(coordinator, storage, writer, 5);
        await coordinator.RollbackToUndoMarkerAsync(writer, outer);
        await coordinator.CommitAsync(writer);

        // Assert against the independent prefix-only oracle after commit.
        using var rows = storage.GetUnitIterator();
        rows.MoveNext().ShouldBeTrue();
        rows.Current.Data.Span[RecordVersionStamp.HeaderSize].ShouldBe((byte)1);
        rows.MoveNext().ShouldBeFalse();
        writer.State.ShouldBe(TransactionState.Committed);
    }

    [Fact(DisplayName = "Cohesion Test [Transactions] - Undo: memory marker controls complete while another writer holds the apply gate and leave deferred checkpoints alone")]
    public async Task CreateAndReleaseMarker_OtherWriterApplying_ShouldCompleteWithoutCheckpoint()
    {
        // Arrange: recorded entry and explicit resume hold the gate, with no duration assertion.
        using var storage = new UndoTestStorage();
        await using var coordinator = new TransactionCoordinator(storage, storage.Log, storage.Records);
        var blocker = await coordinator.BeginAsync(IsolationLevel.Snapshot);
        var writer = await coordinator.BeginAsync(IsolationLevel.Snapshot);
        var released = await coordinator.CreateUndoMarkerAsync(writer);
        using var entered = new ManualResetEventSlim();
        using var resume = new ManualResetEventSlim();
        int checkpoints = 0;
        coordinator.BeforeCheckpoint = _ => checkpoints++;
        var apply = DedicatedTestTask.Run(() => coordinator.ApplyStatementAsync(blocker, _ =>
        {
            entered.Set();
            resume.Wait(_timeout).ShouldBeTrue();
            return 0;
        }).AsTask());
        Task? create = null, release = null;
        try
        {
            entered.Wait(_timeout).ShouldBeTrue();
            coordinator.TryCheckpoint(TimeSpan.Zero, CancellationToken.None).ShouldBeFalse();

            // Act
            create = coordinator.CreateUndoMarkerAsync(writer).AsTask();
            release = coordinator.ReleaseUndoMarkerAsync(writer, released).AsTask();

            // Assert before releasing the gate: neither control queues or checkpoints.
            create.IsCompletedSuccessfully.ShouldBeTrue();
            release.IsCompletedSuccessfully.ShouldBeTrue();
            checkpoints.ShouldBe(0);
        }
        finally
        {
            resume.Set();
            await apply.WaitAsync(_timeout);
            if (create is not null) { await create.WaitAsync(_timeout); }
            if (release is not null) { await release.WaitAsync(_timeout); }
        }
        checkpoints.ShouldBe(1); // the writer that owns the gate runs the deferred checkpoint.
        await coordinator.CommitAsync(writer);
        await coordinator.CommitAsync(blocker);
    }

    private static ValueTask<int> RecordPrefixAsync(TransactionCoordinator coordinator, TransactionContext writer, CountingUndoIndex index)
        => coordinator.ApplyStatementAsync(writer, _ =>
        {
            for (int i = 0; i < 100_000; i++)
            { coordinator.VersionStore.RecordIndexEntryTombstoned(writer.Sequence, index, BitConverter.GetBytes(i), (ulong)i + 1); }
            return 0;
        });

    private static long MeasureUndo(TransactionCoordinator coordinator, TransactionContext writer, TransactionUndoMarker marker)
    {
        long before = GC.GetAllocatedBytesForCurrentThread();
        var undo = coordinator.RollbackToUndoMarkerAsync(writer, marker);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        undo.IsCompletedSuccessfully.ShouldBeTrue();
        undo.GetAwaiter().GetResult();
        return allocated;
    }

    private static ValueTask<ulong> InsertAsync(TransactionCoordinator coordinator, UndoTestStorage storage,
        TransactionContext writer, byte value, UndoTestStorage.Index? index = null)
        => coordinator.ApplyStatementAsync(writer, bracket =>
        {
            var record = new byte[RecordVersionStamp.HeaderSize + 100];
            RecordVersionStamp.WriteWriter(record, writer.Sequence);
            record[RecordVersionStamp.HeaderSize] = value;
            var location = storage.Insert(bracket, record);
            ulong packed = UndoTestStorage.Pack(location.PageId, location.SlotIndex);
            if (index is null) { coordinator.VersionStore.RecordCreated(writer.Sequence, location.PageId, location.SlotIndex); }
            else { coordinator.VersionStore.RecordIndexEntryCreated(writer.Sequence, index, new byte[] { value }, packed); }
            return packed;
        });
}
