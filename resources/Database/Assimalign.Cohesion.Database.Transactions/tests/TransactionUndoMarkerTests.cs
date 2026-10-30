using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Storage;
using Assimalign.Cohesion.Database.Storage.Tests.TestObjects;
using Assimalign.Cohesion.Database.Transactions.Internal;
using Assimalign.Cohesion.Database.Transactions.Tests.TestObjects;

namespace Assimalign.Cohesion.Database.Transactions.Tests;

[Collection("Undo substrate")]
public sealed class TransactionUndoMarkerTests
{
    private static readonly TimeSpan _timeout = TimeSpan.FromSeconds(30);

    [Theory(DisplayName = "Cohesion Test [Transactions] - Undo markers: nested row/index suffixes preserve earlier work and remain open in every storage model")]
    [InlineData(StorageModel.Sql)]
    [InlineData(StorageModel.Document)]
    [InlineData(StorageModel.KeyValue)]
    [InlineData(StorageModel.Blob)]
    [InlineData(StorageModel.Graph)]
    public async Task RollbackToMarker_NestedRowAndIndexEffects_ShouldPreserveEarlierWork(StorageModel model)
    {
        // Arrange
        using var storage = new UndoTestStorage(model);
        await using var coordinator = new TransactionCoordinator(storage, storage.Log, storage.Records);
        var writer = await coordinator.BeginAsync(IsolationLevel.Snapshot);
        var index = new UndoTestStorage.Index(storage);
        ulong earlier = await InsertAsync(coordinator, storage, writer, 1);
        ulong oldRow = await InsertAsync(coordinator, storage, writer, 2);
        ulong oldIndex = await InsertAsync(coordinator, storage, writer, 3, index);
        var outer = await coordinator.CreateUndoMarkerAsync(writer);
        await TombstoneAsync(coordinator, storage, writer, oldRow);
        await TombstoneAsync(coordinator, storage, writer, oldIndex, index);
        ulong middle = await InsertAsync(coordinator, storage, writer, 4);
        var inner = await coordinator.CreateUndoMarkerAsync(writer);
        await InsertAsync(coordinator, storage, writer, 5, index);

        // Act
        await coordinator.RollbackToUndoMarkerAsync(writer, inner);
        Values(storage).ShouldBe([1, 2, 3, 4]);
        await coordinator.RollbackToUndoMarkerAsync(writer, outer);

        // Assert
        writer.State.ShouldBe(TransactionState.Active);
        Values(storage).ShouldBe([1, 2, 3]);
        RecordVersionStamp.ReadStamps(storage.Read(oldRow).Span).Deleter.ShouldBe(TransactionSequence.None);
        RecordVersionStamp.ReadStamps(storage.Read(oldIndex).Span).Deleter.ShouldBe(TransactionSequence.None);
        (await coordinator.VersionStore.GetVisibleVersionAsync(0, earlier, writer.Snapshot)).HasValue.ShouldBeTrue();
        var discarded = await Should.ThrowAsync<TransactionUndoException>(() => coordinator.RollbackToUndoMarkerAsync(writer, inner).AsTask());
        discarded.Code.ShouldBe("CK04008");
        await InsertAsync(coordinator, storage, writer, 6);
        await coordinator.RollbackToUndoMarkerAsync(writer, outer); // target remains reusable
        await coordinator.ReleaseUndoMarkerAsync(writer, outer);
        await coordinator.CommitAsync(writer);
        Values(storage).ShouldBe([1, 2, 3]);
        index.Calls.ShouldBe(2);
        coordinator.VersionStore.TrackedVersionCount.ShouldBe(0);
    }

    [Fact(DisplayName = "Cohesion Test [Transactions] - Undo markers: release merges descendant work and invalidates equal-position identities")]
    public async Task ReleaseMarker_EqualPositionsAndDescendants_ShouldRetainWorkAndInvalidateTokens()
    {
        // Arrange
        using var storage = new UndoTestStorage();
        await using var coordinator = new TransactionCoordinator(storage, storage.Log, storage.Records);
        var writer = await coordinator.BeginAsync(IsolationLevel.Snapshot);
        var first = await coordinator.CreateUndoMarkerAsync(writer);
        var second = await coordinator.CreateUndoMarkerAsync(writer);
        await InsertAsync(coordinator, storage, writer, 7);

        // Act
        await coordinator.ReleaseUndoMarkerAsync(writer, first);

        // Assert
        foreach (var marker in new[] { first, second })
        {
            var failure = await Should.ThrowAsync<TransactionUndoException>(() => coordinator.RollbackToUndoMarkerAsync(writer, marker).AsTask());
            failure.Code.ShouldBe("CK04008");
        }
        await coordinator.CommitAsync(writer);
        Values(storage).ShouldBe([7]);
    }

    [Fact(DisplayName = "Cohesion Test [Transactions] - Undo markers: foreign/stale contexts and markers are refused without poisoning the owner")]
    public async Task CreateMarker_InvalidOwnership_ShouldRefuseWithoutChangingTransaction()
    {
        // Arrange
        using var storage = new UndoTestStorage();
        using var foreignStorage = new UndoTestStorage();
        await using var coordinator = new TransactionCoordinator(storage, storage.Log, storage.Records);
        await using var foreign = new TransactionCoordinator(foreignStorage, foreignStorage.Log, foreignStorage.Records);
        var writer = await coordinator.BeginAsync(IsolationLevel.Snapshot);
        var other = await foreign.BeginAsync(IsolationLevel.Snapshot); // same sequence on another manager
        var marker = await coordinator.CreateUndoMarkerAsync(writer);
        var foreignMarker = await foreign.CreateUndoMarkerAsync(other);

        // Act / Assert
        (await Should.ThrowAsync<TransactionUndoException>(() => coordinator.CreateUndoMarkerAsync(other).AsTask())).Code.ShouldBe("CK04008");
        await Should.ThrowAsync<TransactionUndoException>(() => coordinator.CreateUndoMarkerAsync(writer.PinStatementSnapshot()).AsTask());
        await Should.ThrowAsync<TransactionUndoException>(() => coordinator.RollbackToUndoMarkerAsync(writer, foreignMarker).AsTask());
        await coordinator.RollbackToUndoMarkerAsync(writer, marker);
        await coordinator.CommitAsync(writer);
        await Should.ThrowAsync<TransactionUndoException>(() => coordinator.ReleaseUndoMarkerAsync(writer, marker).AsTask());
        await foreign.RollbackAsync(other);
    }

    [Theory(DisplayName = "Cohesion Test [Transactions] - Undo markers: incomplete undo blocks commit/writes and permits retry or root abort")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RollbackToMarker_IndexFailureAfterCommittedBatch_ShouldRequireRetryOrFullRollback(bool abort)
    {
        // Arrange: reverse undo commits 64 creates, then the index callback fails in the next batch.
        using var storage = new UndoTestStorage();
        await using var coordinator = new TransactionCoordinator(storage, storage.Log, storage.Records);
        var writer = await coordinator.BeginAsync(IsolationLevel.Snapshot);
        await InsertAsync(coordinator, storage, writer, 1);
        var marker = await coordinator.CreateUndoMarkerAsync(writer);
        var index = new UndoTestStorage.Index(storage) { BeforeUndo = () => throw new IOException("Injected undo failure") };
        await InsertAsync(coordinator, storage, writer, 2, index);
        for (int i = 0; i < 70; i++) { await InsertAsync(coordinator, storage, writer, 3); }

        // Act
        await Should.ThrowAsync<IOException>(() => coordinator.RollbackToUndoMarkerAsync(writer, marker).AsTask());

        // Assert
        writer.State.ShouldBe(TransactionState.Active);
        writer.PartialUndoPending.ShouldBeTrue();
        coordinator.VersionStore.TrackedVersionCount.ShouldBe(8); // prefix + 7 outstanding
        (await Should.ThrowAsync<TransactionUndoException>(() => coordinator.CommitAsync(writer).AsTask())).Code.ShouldBe("CK04009");
        (await Should.ThrowAsync<TransactionUndoException>(() => InsertAsync(coordinator, storage, writer, 9).AsTask())).Code.ShouldBe("CK04009");
        index.BeforeUndo = null;
        if (abort)
        {
            await coordinator.RollbackAsync(writer);
            Values(storage).ShouldBeEmpty();
        }
        else
        {
            await coordinator.RollbackToUndoMarkerAsync(writer, marker);
            writer.PartialUndoPending.ShouldBeFalse();
            await coordinator.CommitAsync(writer);
            Values(storage).ShouldBe([1]);
        }
    }

    [Fact(DisplayName = "Cohesion Test [Transactions] - Undo markers: repeated row/index tombstones preserve the pre-marker deletion")]
    public async Task RollbackToMarker_RepeatedTombstones_ShouldPreserveEarlierDeletion()
    {
        // Arrange
        using var storage = new UndoTestStorage();
        await using var coordinator = new TransactionCoordinator(storage, storage.Log, storage.Records);
        var writer = await coordinator.BeginAsync(IsolationLevel.Snapshot);
        var index = new UndoTestStorage.Index(storage);
        ulong row = await InsertAsync(coordinator, storage, writer, 1);
        ulong entry = await InsertAsync(coordinator, storage, writer, 2, index);
        await TombstoneAsync(coordinator, storage, writer, row);
        await TombstoneAsync(coordinator, storage, writer, entry, index);
        var marker = await coordinator.CreateUndoMarkerAsync(writer);
        await TombstoneAsync(coordinator, storage, writer, row);
        await TombstoneAsync(coordinator, storage, writer, entry, index);

        // Act
        await coordinator.RollbackToUndoMarkerAsync(writer, marker);

        // Assert
        RecordVersionStamp.ReadStamps(storage.Read(row).Span).Deleter.ShouldBe(writer.Sequence);
        RecordVersionStamp.ReadStamps(storage.Read(entry).Span).Deleter.ShouldBe(writer.Sequence);
        index.Calls.ShouldBe(0);
        await coordinator.RollbackAsync(writer);
        Values(storage).ShouldBeEmpty();
    }

    [Theory(DisplayName = "Cohesion Test [Transactions] - Undo markers: failed tombstones before the first marker cannot protect a later deletion")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RollbackToMarker_FailedTombstoneBeforeFirstMarker_ShouldRestoreLaterDeletion(bool indexed)
    {
        // Arrange
        using var storage = new UndoTestStorage();
        await using var coordinator = new TransactionCoordinator(storage, storage.Log, storage.Records);
        var index = indexed ? new UndoTestStorage.Index(storage) : null;
        var seeded = await coordinator.BeginAsync(IsolationLevel.Snapshot);
        ulong old = await InsertAsync(coordinator, storage, seeded, 1, index);
        await coordinator.CommitAsync(seeded);
        var writer = await coordinator.BeginAsync(IsolationLevel.Snapshot);
        await Should.ThrowAsync<IOException>(() => coordinator.ApplyStatementAsync<int>(writer, bracket =>
        {
            storage.Update(bracket, old, RecordVersionStamp.WithDeleter(storage.Read(old).Span, writer.Sequence));
            var (page, slot) = UndoTestStorage.Unpack(old);
            if (index is null) { coordinator.VersionStore.RecordTombstoned(writer.Sequence, page, slot); }
            else { coordinator.VersionStore.RecordIndexEntryTombstoned(writer.Sequence, index, new byte[] { 1 }, old); }
            throw new IOException("Injected statement failure before any marker");
        }, CancellationToken.None).AsTask());
        var marker = await coordinator.CreateUndoMarkerAsync(writer);
        await TombstoneAsync(coordinator, storage, writer, old, index);

        // Act
        await coordinator.RollbackToUndoMarkerAsync(writer, marker);

        // Assert: the failed statement's ledger entry is absent from the retained prefix.
        RecordVersionStamp.ReadStamps(storage.Read(old).Span).Deleter.ShouldBe(TransactionSequence.None);
        (await coordinator.VersionStore.GetVisibleVersionAsync(0, old, writer.Snapshot)).HasValue.ShouldBeTrue();
        coordinator.VersionStore.TrackedVersionCount.ShouldBe(0);
        await coordinator.CommitAsync(writer);
        Values(storage).ShouldBe([1]);
    }

    [Fact(DisplayName = "Cohesion Test [Transactions] - Undo markers: failed statement entries cannot undo same-writer slot reuse before a later marker")]
    public async Task RollbackToMarker_FailedStatementAndSlotReuse_ShouldDiscardFailedLedgerSuffix()
    {
        // Arrange
        using var storage = new UndoTestStorage();
        await using var coordinator = new TransactionCoordinator(storage, storage.Log, storage.Records);
        var writer = await coordinator.BeginAsync(IsolationLevel.Snapshot);
        var outer = await coordinator.CreateUndoMarkerAsync(writer);
        await Should.ThrowAsync<IOException>(() => coordinator.ApplyStatementAsync<int>(writer, bracket =>
        {
            var location = storage.Insert(bracket, Stamped(writer.Sequence, 8));
            coordinator.VersionStore.RecordCreated(writer.Sequence, location.PageId, location.SlotIndex);
            throw new IOException("Injected statement failure");
        }, CancellationToken.None).AsTask());
        await InsertAsync(coordinator, storage, writer, 1);
        var inner = await coordinator.CreateUndoMarkerAsync(writer);
        await InsertAsync(coordinator, storage, writer, 2);

        // Act
        await coordinator.RollbackToUndoMarkerAsync(writer, inner);

        // Assert
        Values(storage).ShouldBe([1]);
        coordinator.VersionStore.TrackedVersionCount.ShouldBe(1);
        await coordinator.RollbackToUndoMarkerAsync(writer, outer);
        Values(storage).ShouldBeEmpty();
    }

    [Fact(DisplayName = "Cohesion Test [Transactions] - Undo markers: cancellation before start leaves state, cancellation after start completes undo and retains locks")]
    public async Task RollbackToMarker_Cancellation_ShouldOnlyCancelBeforeUndoStarts()
    {
        // Arrange
        using var storage = new UndoTestStorage();
        await using var coordinator = new TransactionCoordinator(storage, storage.Log, storage.Records);
        var writer = await coordinator.BeginAsync(IsolationLevel.Snapshot);
        var marker = await coordinator.CreateUndoMarkerAsync(writer);
        using var canceled = new CancellationTokenSource();
        var index = new UndoTestStorage.Index(storage) { BeforeUndo = canceled.Cancel };
        await InsertAsync(coordinator, storage, writer, 4, index);
        await coordinator.LockManager.AcquireAsync(writer.Sequence, LockResource.Entry(1, 1), LockMode.Exclusive);
        var other = await coordinator.BeginAsync(IsolationLevel.Snapshot);
        var wait = coordinator.LockManager.AcquireAsync(other.Sequence, LockResource.Entry(1, 1), LockMode.Exclusive).AsTask();

        // Act
        await Should.ThrowAsync<OperationCanceledException>(() => coordinator.RollbackToUndoMarkerAsync(writer, marker, new CancellationToken(true)).AsTask());
        Values(storage).ShouldBe([4]);
        await coordinator.RollbackToUndoMarkerAsync(writer, marker, canceled.Token);

        // Assert
        canceled.IsCancellationRequested.ShouldBeTrue();
        Values(storage).ShouldBeEmpty();
        wait.IsCompleted.ShouldBeFalse();
        writer.State.ShouldBe(TransactionState.Active);
        await coordinator.CommitAsync(writer);
        await wait.WaitAsync(_timeout);
        await coordinator.RollbackAsync(other);
    }

    [Theory(DisplayName = "Cohesion Test [Transactions] - Undo markers: root rollback and disposal drain an admitted partial undo")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RollbackToMarker_ConcurrentRootEnd_ShouldDrainBeforeReleasingState(bool dispose)
    {
        // Arrange
        using var storage = new UndoTestStorage();
        await using var coordinator = new TransactionCoordinator(storage, storage.Log, storage.Records);
        var writer = await coordinator.BeginAsync(IsolationLevel.Snapshot);
        var marker = await coordinator.CreateUndoMarkerAsync(writer);
        using var entered = new ManualResetEventSlim();
        using var resume = new ManualResetEventSlim();
        var index = new UndoTestStorage.Index(storage)
        {
            BeforeUndo = () => { entered.Set(); resume.Wait(_timeout).ShouldBeTrue(); }
        };
        await InsertAsync(coordinator, storage, writer, 4, index);
        var undo = DedicatedTestTask.Run(() => coordinator.RollbackToUndoMarkerAsync(writer, marker).AsTask());
        Task? end = null;
        try
        {
            entered.Wait(_timeout).ShouldBeTrue();
            (await Should.ThrowAsync<TransactionUndoException>(() => coordinator.CommitAsync(writer).AsTask())).Code.ShouldBe("CK04009");
            (await Should.ThrowAsync<TransactionUndoException>(() => coordinator.CreateUndoMarkerAsync(writer).AsTask())).Code.ShouldBe("CK04009");
            await Should.ThrowAsync<OperationCanceledException>(() => coordinator.CreateUndoMarkerAsync(writer, new CancellationToken(true)).AsTask());

            // Act
            end = dispose ? coordinator.DisposeAsync().AsTask() : coordinator.RollbackAsync(writer).AsTask();
            end.IsCompleted.ShouldBeFalse();
        }
        finally
        {
            resume.Set();
            await undo.WaitAsync(_timeout);
            if (end is not null) { await end.WaitAsync(_timeout); }
        }

        // Assert
        writer.State.ShouldBe(dispose ? TransactionState.Faulted : TransactionState.RolledBack);
        coordinator.GetOpenContexts().ShouldBeEmpty();
        Values(storage).ShouldBeEmpty();
        coordinator.VersionStore.TrackedVersionCount.ShouldBe(0);
        await Should.ThrowAsync<TransactionUndoException>(() => coordinator.RollbackToUndoMarkerAsync(writer, marker).AsTask());
    }

    [Fact(DisplayName = "Cohesion Test [Transactions] - Undo markers: even a fatal deferred-checkpoint exception releases control admission and the apply gate")]
    public async Task RollbackToMarker_DeferredCheckpointThrows_ShouldReleaseGateAndAdmission()
    {
        // Arrange
        using var storage = new UndoTestStorage();
        await using var coordinator = new TransactionCoordinator(storage, storage.Log, storage.Records);
        var writer = await coordinator.BeginAsync(IsolationLevel.Snapshot);
        var marker = await coordinator.CreateUndoMarkerAsync(writer);
        using var entered = new ManualResetEventSlim();
        using var resume = new ManualResetEventSlim();
        var index = new UndoTestStorage.Index(storage)
        {
            BeforeUndo = () => { entered.Set(); resume.Wait(_timeout).ShouldBeTrue(); }
        };
        await InsertAsync(coordinator, storage, writer, 1, index);
        int checkpoints = 0;
        coordinator.BeforeCheckpoint = _ => { checkpoints++; throw new OutOfMemoryException("Injected checkpoint failure"); };
        var undo = DedicatedTestTask.Run(() => coordinator.RollbackToUndoMarkerAsync(writer, marker).AsTask());
        try
        {
            entered.Wait(_timeout).ShouldBeTrue();
            coordinator.TryCheckpoint(TimeSpan.Zero, CancellationToken.None).ShouldBeFalse();
        }
        finally { resume.Set(); }

        // Act
        await Should.ThrowAsync<OutOfMemoryException>(() => undo.WaitAsync(_timeout));
        coordinator.BeforeCheckpoint = null;
        await coordinator.CreateUndoMarkerAsync(writer).AsTask().WaitAsync(_timeout);

        // Assert
        checkpoints.ShouldBe(1);
        writer.PartialUndoPending.ShouldBeFalse();
        Values(storage).ShouldBeEmpty();
        await coordinator.CommitAsync(writer);
    }

    [Fact(DisplayName = "Cohesion Test [Transactions] - Undo markers: empty markers remain memory-only and stale isolation handles are refused")]
    public async Task CreateMarker_ReadOnlyAndReplacedContext_ShouldWriteNoLifecycleRecords()
    {
        // Arrange
        using var storage = new UndoTestStorage();
        await using var coordinator = new TransactionCoordinator(storage, storage.Log, storage.Records);
        var writer = await coordinator.BeginAsync(IsolationLevel.Snapshot);
        var before = storage.Log.ReadAll().Count;
        var marker = await coordinator.CreateUndoMarkerAsync(writer);
        var replacement = coordinator.ReplaceIsolationLevel(writer, IsolationLevel.ReadCommitted);

        // Act
        await Should.ThrowAsync<TransactionUndoException>(() => coordinator.CreateUndoMarkerAsync(writer).AsTask());
        await coordinator.RollbackToUndoMarkerAsync(replacement, marker);
        await coordinator.ReleaseUndoMarkerAsync(replacement, marker);
        await coordinator.CommitAsync(replacement);

        // Assert
        storage.Log.ReadAll().Count.ShouldBe(before);
        writer.State.ShouldBe(TransactionState.Committed);
        Values(storage).ShouldBeEmpty();
    }

    [Theory(DisplayName = "Cohesion Test [Transactions] - Undo markers: crash after partial undo recovers exactly the committed prefix")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RollbackToMarker_CrashWithOrWithoutCommit_ShouldRecoverCommittedState(bool commit)
    {
        // Arrange
        using var storage = new UndoTestStorage();
        await using var coordinator = new TransactionCoordinator(storage, storage.Log, storage.Records);
        var seeded = await coordinator.BeginAsync(IsolationLevel.Snapshot);
        ulong old = await InsertAsync(coordinator, storage, seeded, 1);
        await coordinator.CommitAsync(seeded);
        var writer = await coordinator.BeginAsync(IsolationLevel.Snapshot);
        await InsertAsync(coordinator, storage, writer, 2);
        var marker = await coordinator.CreateUndoMarkerAsync(writer);
        await TombstoneAsync(coordinator, storage, writer, old);
        await InsertAsync(coordinator, storage, writer, 3, new UndoTestStorage.Index(storage));

        // Act
        await coordinator.RollbackToUndoMarkerAsync(writer, marker);
        coordinator.Checkpoint(); // anchors the still-open root writer across truncation
        if (commit) { await coordinator.CommitAsync(writer); }
        storage.FlushDurable();
        var image = storage.Capture();

        // Assert
        await RecoverAsync(image, commit ? new byte[] { 1, 2 } : new byte[] { 1 });
    }

    [Fact(DisplayName = "Cohesion Test [Transactions] - Undo markers: every whole/torn write during undo recovers the pre-transaction state")]
    public async Task RollbackToMarker_CrashAtEveryUndoWrite_ShouldRecoverRootAbort()
    {
        // Arrange: enough rows for multiple undo batches and a tiny pool for steals.
        (byte[] Data, byte[] Journal) image;
        using (var storage = new UndoTestStorage(poolCapacity: 3))
        {
            await using var coordinator = new TransactionCoordinator(storage, storage.Log, storage.Records);
            var seeded = await coordinator.BeginAsync(IsolationLevel.Snapshot);
            await InsertAsync(coordinator, storage, seeded, 1);
            await coordinator.CommitAsync(seeded);
            storage.FlushDurable();
            image = storage.Capture();
        }
        var dryPoint = new CrashPoint();
        int writes;
        using (var dry = new UndoTestStorage(image: image, point: dryPoint, poolCapacity: 3))
        {
            await using var coordinator = new TransactionCoordinator(dry, dry.Log, dry.Records);
            coordinator.AnalyzeAndScrub();
            coordinator.CompleteRecovery();
            var writer = await coordinator.BeginAsync(IsolationLevel.Snapshot);
            var marker = await coordinator.CreateUndoMarkerAsync(writer);
            for (int i = 0; i < 130; i++) { await InsertAsync(coordinator, dry, writer, 2, payloadSize: 1500); }
            int start = dryPoint.Writes;
            await coordinator.RollbackToUndoMarkerAsync(writer, marker);
            (dryPoint.Writes - start).ShouldBeGreaterThan(2); // force writes inside undo, before flush
            dry.FlushDurable();
            writes = dryPoint.Writes - start;
        }
        writes.ShouldBeGreaterThan(2);

        // Act / Assert
        for (int write = 1; write <= writes; write++)
        {
            foreach (int sectors in new[] { 0, 3 })
            {
                var point = new CrashPoint { DurableSectors = sectors };
                var storage = new UndoTestStorage(image: image, point: point, poolCapacity: 3);
                var coordinator = new TransactionCoordinator(storage, storage.Log, storage.Records);
                try
                {
                    coordinator.AnalyzeAndScrub();
                    coordinator.CompleteRecovery();
                    var writer = await coordinator.BeginAsync(IsolationLevel.Snapshot);
                    var marker = await coordinator.CreateUndoMarkerAsync(writer);
                    for (int i = 0; i < 130; i++) { await InsertAsync(coordinator, storage, writer, 2, payloadSize: 1500); }
                    point.CrashAtWrite = point.Writes + write;
                    var failure = await Should.ThrowAsync<Exception>(async () =>
                    {
                        await coordinator.RollbackToUndoMarkerAsync(writer, marker);
                        storage.FlushDurable();
                    });
                    failure.GetBaseException().ShouldBeOfType<SimulatedPowerLossException>();
                    point.HasCrashed.ShouldBeTrue();
                    await RecoverAsync(storage.Capture(), [1]);
                }
                finally
                {
                    // Closing after simulated power loss cannot write; every handle is still closed.
                    try { await coordinator.DisposeAsync(); } catch (Exception exception) when (point.HasCrashed && (exception.GetBaseException() is SimulatedPowerLossException || exception is StorageTransactionException)) { }
                    try { storage.Dispose(); } catch (Exception exception) when (point.HasCrashed && (exception.GetBaseException() is SimulatedPowerLossException || exception is StorageTransactionException)) { }
                }
            }
        }
    }

    [Fact(DisplayName = "Cohesion Test [Transactions] - Undo markers: readers and purge overlap repeated multi-batch undo without exposing partial effects")]
    public async Task RollbackToMarker_ConcurrentReadersAndPurge_ShouldKeepVisibilityAndHorizon()
    {
        // Arrange
        using var storage = new UndoTestStorage();
        await using var coordinator = new TransactionCoordinator(storage, storage.Log, storage.Records);
        var seeded = await coordinator.BeginAsync(IsolationLevel.Snapshot);
        ulong old = await InsertAsync(coordinator, storage, seeded, 1);
        await coordinator.CommitAsync(seeded);
        var writer = await coordinator.BeginAsync(IsolationLevel.Snapshot);
        // Re-capture visibility on every read, so an accidentally released root writer
        // would immediately expose its surviving suffix or hide the committed row.
        var reader = await coordinator.BeginAsync(IsolationLevel.ReadCommitted);
        var marker = await coordinator.CreateUndoMarkerAsync(writer);
        long reads = 0, overlaps = 0, undos = 0, purges = 0, newLocation = 0;
        int undoing = 0;
        var index = new UndoTestStorage.Index(storage)
        {
            BeforeUndo = () =>
            {
                // Entered from inside a real undo bracket, after its newer row deletes.
                // Require a reader to cross this exact window; gate-wait time does not count.
                long before = Interlocked.Read(ref overlaps);
                Volatile.Write(ref undoing, 1);
                try { SpinWait.SpinUntil(() => Interlocked.Read(ref overlaps) > before, _timeout).ShouldBeTrue(); }
                finally { Volatile.Write(ref undoing, 0); }
            }
        };
        using var stop = new CancellationTokenSource();
        using var started = new ManualResetEventSlim();
        var readerTask = DedicatedTestTask.Run(async () =>
        {
            started.Set();
            while (!stop.IsCancellationRequested)
            {
                (await coordinator.VersionStore.GetVisibleVersionAsync(0, old, reader.Snapshot)).HasValue.ShouldBeTrue();
                long created = Interlocked.Read(ref newLocation);
                if (created != 0)
                {
                    (await coordinator.VersionStore.GetVisibleVersionAsync(0, (ulong)created, reader.Snapshot)).HasValue.ShouldBeFalse();
                }
                Interlocked.Increment(ref reads);
                if (Volatile.Read(ref undoing) != 0) { Interlocked.Increment(ref overlaps); }
            }
        });
        var purgeTask = DedicatedTestTask.Run(() =>
        {
            while (!stop.IsCancellationRequested)
            {
                coordinator.RunVersionPurgePass(stop.Token);
                Interlocked.Increment(ref purges);
            }
        });
        var clock = Stopwatch.StartNew();

        // Act: minimum duration plus recorded race progress; the ceiling only diagnoses hangs.
        try
        {
            started.Wait(_timeout).ShouldBeTrue();
            do
            {
                await TombstoneAsync(coordinator, storage, writer, old);
                await InsertAsync(coordinator, storage, writer, 3, index);
                for (int i = 0; i < 70; i++)
                {
                    ulong created = await InsertAsync(coordinator, storage, writer, 2);
                    Interlocked.Exchange(ref newLocation, (long)created);
                }
                await coordinator.RollbackToUndoMarkerAsync(writer, marker);
                Interlocked.Increment(ref undos);
            }
            while ((clock.Elapsed < TimeSpan.FromMilliseconds(250) || Interlocked.Read(ref overlaps) < 50 || Interlocked.Read(ref purges) == 0)
                && clock.Elapsed < _timeout);
        }
        finally
        {
            stop.Cancel();
            try { await readerTask.WaitAsync(_timeout); }
            finally
            {
                try { await purgeTask.WaitAsync(_timeout); } catch (OperationCanceledException) { }
            }
        }

        // Assert
        reads.ShouldBeGreaterThan(0);
        overlaps.ShouldBeGreaterThanOrEqualTo(50);
        undos.ShouldBeGreaterThan(0);
        purges.ShouldBeGreaterThan(0);
        coordinator.PruneBound.ShouldBeLessThanOrEqualTo(writer.Sequence);
        Values(storage).ShouldBe([1]);
        await coordinator.CommitAsync(writer);
        await coordinator.CommitAsync(reader);
    }

    private static ValueTask<ulong> InsertAsync(TransactionCoordinator coordinator, UndoTestStorage storage,
        TransactionContext context, byte value, UndoTestStorage.Index? index = null, int payloadSize = 100)
        => coordinator.ApplyStatementAsync(context, bracket =>
        {
            var location = storage.Insert(bracket, Stamped(context.Sequence, value, payloadSize));
            ulong packed = UndoTestStorage.Pack(location.PageId, location.SlotIndex);
            if (index is null) { coordinator.VersionStore.RecordCreated(context.Sequence, location.PageId, location.SlotIndex); }
            else { coordinator.VersionStore.RecordIndexEntryCreated(context.Sequence, index, new byte[] { value }, packed); }
            return packed;
        });

    private static async Task TombstoneAsync(TransactionCoordinator coordinator, UndoTestStorage storage,
        TransactionContext context, ulong location, UndoTestStorage.Index? index = null)
        => _ = await coordinator.ApplyStatementAsync(context, bracket =>
        {
            storage.Update(bracket, location, RecordVersionStamp.WithDeleter(storage.Read(location).Span, context.Sequence));
            var (page, slot) = UndoTestStorage.Unpack(location);
            if (index is null) { coordinator.VersionStore.RecordTombstoned(context.Sequence, page, slot); }
            else { coordinator.VersionStore.RecordIndexEntryTombstoned(context.Sequence, index, new byte[] { 1 }, location); }
            return 0;
        });

    private static byte[] Stamped(TransactionSequence writer, byte value, int payloadSize = 100)
    {
        var record = new byte[RecordVersionStamp.HeaderSize + payloadSize];
        RecordVersionStamp.WriteWriter(record, writer);
        record[RecordVersionStamp.HeaderSize] = value;
        return record;
    }

    private static byte[] Values(UndoTestStorage storage)
    {
        var values = new System.Collections.Generic.List<byte>();
        using var iterator = storage.GetUnitIterator();
        while (iterator.MoveNext()) { values.Add(iterator.Current.Data.Span[RecordVersionStamp.HeaderSize]); }
        return values.Order().ToArray();
    }

    private static async Task RecoverAsync((byte[] Data, byte[] Journal) image, byte[] expected)
    {
        using var reopened = new UndoTestStorage(image: image, poolCapacity: 3);
        await using var recovered = new TransactionCoordinator(reopened, reopened.Log, reopened.Records);
        recovered.AnalyzeAndScrub();
        recovered.CompleteRecovery();
        Values(reopened).ShouldBe(expected);
    }
}
