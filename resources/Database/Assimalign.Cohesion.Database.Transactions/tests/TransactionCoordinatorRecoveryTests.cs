using System;
using System.Buffers.Binary;
using System.Diagnostics;
using System.Diagnostics.Tracing;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Shouldly;
using Xunit;

using Assimalign.Cohesion.Testing;
using Assimalign.Cohesion.Database.Transactions.Internal;

using Assimalign.Cohesion.Database.Storage;
using Assimalign.Cohesion.Database.Storage.Tests;

using Assimalign.Cohesion.Database.Storage.Tests.TestObjects;

namespace Assimalign.Cohesion.Database.Transactions.Tests;

/// <summary>
/// Exercises the shared coordinator's recovery ordering and snapshot purge bound
/// against real record pages and a real write-ahead journal.
/// </summary>
[Collection("Journal enrollment")]
public partial class TransactionCoordinatorRecoveryTests
{
    [Theory(DisplayName = "Cohesion Test [Transactions] - Raw scopes service deferred checkpoints on every exit and nested disposal (#1283)")]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task TryCheckpoint_RawScopeExit_ShouldServiceItsDeferredCheckpoint(int exit)
    {
        // Arrange: the coordinator and raw scope must resolve exactly the same interlock.
        using var storage = CoordinatorStorage.Create();
        await using var coordinator = new TransactionCoordinator(storage, storage.Log, storage.Records);
        var interlock = TransactionCheckpointInterlock.ForStorage(storage);
        TransactionCheckpointInterlock.ForStorage(storage).ShouldBeSameAs(interlock);
        using var ceiling = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var canceled = new CancellationTokenSource();
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        using var recorder = new EventSourceRecorder(TransactionEventSource.Log, EventLevel.Verbose);
        int passes = 0;
        coordinator.BeforeCheckpoint = _ => Interlocked.Increment(ref passes);
        var expected = new InvalidOperationException("Raw scope failed.");
        var holder = Task.Factory.StartNew(() =>
        {
            using var scope = interlock.Enter("Catalog", 42, ceiling.Token);
            using var bracket = storage.BeginTransaction("Catalog", 42);
            using var nested = interlock.Enter("Nested", 43, ceiling.Token);
            entered.Set();
            release.Wait(ceiling.Token);
            if (exit == 1) { throw expected; }
            if (exit == 2) { canceled.Cancel(); canceled.Token.ThrowIfCancellationRequested(); }
            if (exit == 3) { nested.Dispose(); nested.Dispose(); return; }
            bracket.Commit();
        }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        try
        {
            entered.Wait(ceiling.Token);
            // Act: no new writer is needed to collect this request.
            coordinator.TryCheckpoint(TimeSpan.Zero, ceiling.Token).ShouldBeFalse();
            release.Set();
            if (exit == 1) { (await Should.ThrowAsync<InvalidOperationException>(() => holder)).ShouldBeSameAs(expected); }
            else if (exit == 2) { await Should.ThrowAsync<OperationCanceledException>(() => holder); }
            else { await holder.WaitAsync(ceiling.Token); }

            // Assert: nested release cannot run the checkpoint until the outer bracket ended.
            Volatile.Read(ref passes).ShouldBe(1);
            var blocked = recorder.Events.Where(e => e.EventId == 21 && Equals(e.Payload![0], (string)storage.Name)).ShouldHaveSingleItem();
            blocked.PayloadNames.ShouldBe(new[] { "database", "bracketClass", "owner" });
            blocked.Payload.ShouldBe(new object[] { (string)storage.Name, "Catalog", 42L });
            recorder.Events.ShouldNotContain(e => e.EventId == 0);
            coordinator.TryCheckpoint(TimeSpan.Zero, ceiling.Token).ShouldBeTrue();
            Volatile.Read(ref passes).ShouldBe(2);
        }
        finally
        {
            release.Set();
            try { await holder.WaitAsync(ceiling.Token); }
            catch (InvalidOperationException) when (exit == 1) { }
            catch (OperationCanceledException) when (exit == 2) { }
        }
    }

    [Fact(DisplayName = "Cohesion Test [Transactions] - Explicit checkpoint collects an older busy deferred request once (#1283)")]
    public async Task Checkpoint_OlderBusyDeferral_ShouldClearTheCollectedRequest()
    {
        // Arrange: an outside test bracket makes the first deferred attempt busy.
        using var storage = CoordinatorStorage.Create();
        await using var coordinator = new TransactionCoordinator(storage, storage.Log, storage.Records);
        using var outside = storage.BeginTransaction();
        using var ceiling = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        int passes = 0;
        coordinator.BeforeCheckpoint = _ => Interlocked.Increment(ref passes);
        var holder = Task.Factory.StartNew(() =>
        {
            using var scope = TransactionCheckpointInterlock.ForStorage(storage).Enter("Catalog");
            entered.Set();
            release.Wait(ceiling.Token);
        }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        try
        {
            entered.Wait(ceiling.Token);
            coordinator.TryCheckpoint(TimeSpan.Zero, ceiling.Token).ShouldBeFalse();
            release.Set();
            await holder.WaitAsync(ceiling.Token);
            Volatile.Read(ref passes).ShouldBe(1);
            outside.Rollback();
            // Act
            coordinator.Checkpoint(ceiling.Token);
            // Assert: an explicit checkpoint consumes the older request, rather than running twice.
            Volatile.Read(ref passes).ShouldBe(2);
        }
        finally
        {
            release.Set();
            await holder.WaitAsync(ceiling.Token);
        }
    }

    [Fact(DisplayName = "Cohesion Test [Transactions] - Deferred checkpoint: attribute its own gate hold after the bracket ended (#1283)")]
    public async Task TryCheckpoint_DeferredCheckpointInProgress_ShouldAttributeCheckpointRatherThanEndedBracket()
    {
        // Arrange
        using var storage = CoordinatorStorage.Create();
        await using var coordinator = new TransactionCoordinator(storage, storage.Log, storage.Records);
        var interlock = TransactionCheckpointInterlock.ForStorage(storage);
        using var ceiling = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        using var checkpointEntered = new ManualResetEventSlim();
        using var checkpointRelease = new ManualResetEventSlim();
        using var recorder = new EventSourceRecorder(TransactionEventSource.Log, EventLevel.Verbose);
        int passes = 0;
        coordinator.BeforeCheckpoint = _ =>
        {
            if (Interlocked.Increment(ref passes) != 1) { return; }
            checkpointEntered.Set();
            checkpointRelease.Wait(ceiling.Token);
        };
        var holder = Task.Factory.StartNew(() =>
        {
            using var scope = interlock.Enter("Catalog", 42);
            using var bracket = storage.BeginTransaction("Catalog", 42);
            entered.Set();
            release.Wait(ceiling.Token);
            bracket.Commit();
        }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        try
        {
            entered.Wait(ceiling.Token);
            coordinator.TryCheckpoint(TimeSpan.Zero, ceiling.Token).ShouldBeFalse();
            release.Set();
            checkpointEntered.Wait(ceiling.Token);
            // Act: the physical bracket ended; its releasing flow now holds a checkpoint.
            coordinator.TryCheckpoint(TimeSpan.Zero, ceiling.Token).ShouldBeFalse();
            checkpointRelease.Set();
            await holder.WaitAsync(ceiling.Token);
            coordinator.TryCheckpoint(TimeSpan.Zero, ceiling.Token).ShouldBeTrue();
            // Assert
            var blocked = recorder.Events.Where(e => e.EventId == 21).ToArray();
            blocked.Select(e => (string)e.Payload![1]!).ShouldBe(new[] { "Catalog", "Checkpoint" });
            blocked[1].Payload![2].ShouldBe(0L);
            recorder.Events.ShouldNotContain(e => e.EventId == 0);
        }
        finally
        {
            release.Set(); checkpointRelease.Set();
            await holder.WaitAsync(ceiling.Token);
        }
    }

    [Fact(DisplayName = "Cohesion Test [Transactions] - Interlock: nested raw scope shares coordinated apply and refuses recursive waits (#1283)")]
    public async Task ApplyStatement_NestedRawScope_ShouldShareItsOwningAdmission()
    {
        // Arrange
        using var storage = CoordinatorStorage.Create();
        await using var coordinator = new TransactionCoordinator(storage, storage.Log, storage.Records);
        var context = await coordinator.BeginAsync(IsolationLevel.Snapshot);
        var interlock = TransactionCheckpointInterlock.ForStorage(storage);
        using var ceiling = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        // Act / Assert
        await coordinator.ApplyStatementAsync(context, _ =>
        {
            using var nested = interlock.Enter("NestedRaw");
            Should.Throw<StorageTransactionException>(() => interlock.TryCheckpoint(Timeout.InfiniteTimeSpan));
            return 0;
        }, ceiling.Token);
        await coordinator.CommitAsync(context, ceiling.Token);
        using (interlock.Enter("OuterRaw"))
        { Should.Throw<StorageTransactionException>(() => coordinator.Checkpoint(ceiling.Token)); }
        interlock.TryCheckpoint(TimeSpan.Zero, ceiling.Token).ShouldBeTrue();
    }

    [Fact(DisplayName = "Cohesion Test [Transactions] - Interlock: canceled entry and self-checkpoint refusal release every acquisition (#1283)")]
    public async Task Enter_CanceledWaitAndSelfCheckpoint_ShouldLeaveTheGateUsable()
    {
        // Arrange
        using var storage = CoordinatorStorage.Create();
        var interlock = TransactionCheckpointInterlock.ForStorage(storage);
        using var ceiling = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var canceled = new CancellationTokenSource();
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var holder = Task.Factory.StartNew(() =>
        {
            using var scope = interlock.Enter("Catalog");
            Should.Throw<StorageTransactionException>(() => interlock.TryCheckpoint(Timeout.InfiniteTimeSpan));
            entered.Set();
            release.Wait(ceiling.Token);
        }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        Task? waiting = null;
        try
        {
            entered.Wait(ceiling.Token);
            // Act: cancellation cannot own, release or steal the holder's acquisition.
            var queued = new TaskCompletionSource<Thread>(TaskCreationOptions.RunContinuationsAsynchronously);
            waiting = Task.Factory.StartNew(() =>
            {
                queued.SetResult(Thread.CurrentThread);
                using var scope = interlock.Enter("Waiter", cancellationToken: canceled.Token);
            }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
            Thread waiter = await queued.Task.WaitAsync(ceiling.Token);
            while ((waiter.ThreadState & System.Threading.ThreadState.WaitSleepJoin) == 0 && !waiting.IsCompleted)
            { await Task.Delay(1, ceiling.Token); }
            waiting.IsCompleted.ShouldBeFalse();
            canceled.Cancel();
            await Should.ThrowAsync<OperationCanceledException>(() => waiting);
            interlock.TryCheckpoint(TimeSpan.Zero, ceiling.Token).ShouldBeFalse();
        }
        finally
        {
            canceled.Cancel();
            release.Set();
            await holder.WaitAsync(ceiling.Token);
            if (waiting is not null)
            {
                try { await waiting.WaitAsync(ceiling.Token); }
                catch (OperationCanceledException) { }
            }
        }
        // Assert
        interlock.TryCheckpoint(TimeSpan.Zero, ceiling.Token).ShouldBeTrue();
        using (interlock.Enter("AfterCancellation")) { }
    }

    [Fact(DisplayName = "Cohesion Test [Transactions] - Interlock: uncontended coordinator admission allocates no more than its semaphore control (#1283)")]
    public async Task Wait_UncontendedAdmission_ShouldAllocateZeroBytes()
    {
        // Arrange: minimum of three warmed rounds; no time or throughput threshold.
        using var storage = CoordinatorStorage.Create();
        await using var coordinator = new TransactionCoordinator(storage, storage.Log, storage.Records);
        var interlock = TransactionCheckpointInterlock.ForStorage(storage);
        using var control = new SemaphoreSlim(1, 1);
        interlock.JournalSizeLimit = 1024L * 1024 * 1024;
        using var ceiling = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        CancellationToken token = ceiling.Token;
        long admission = long.MaxValue;
        long baseline = long.MaxValue;
        long asyncAdmission = long.MaxValue;
        long asyncBaseline = long.MaxValue;
        long guardedAdmission = long.MaxValue;
        long guardedAsyncAdmission = long.MaxValue;
        // Act
        for (int round = 0; round < 4; round++)
        {
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 1024; i++) { control.Wait(token); control.Release(); }
            long oldBytes = GC.GetAllocatedBytesForCurrentThread() - before;
            before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 1024; i++) { interlock.Wait(token); interlock.Release(); }
            long newBytes = GC.GetAllocatedBytesForCurrentThread() - before;
            before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 1024; i++) { await control.WaitAsync(token); control.Release(); }
            long oldAsyncBytes = GC.GetAllocatedBytesForCurrentThread() - before;
            before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 1024; i++) { await interlock.WaitAsync(token); interlock.Release(); }
            long newAsyncBytes = GC.GetAllocatedBytesForCurrentThread() - before;
            before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 1024; i++) { interlock.Wait(token, waitForAdmission: false); interlock.Release(); }
            long guardedBytes = GC.GetAllocatedBytesForCurrentThread() - before;
            before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 1024; i++) { await interlock.WaitAsync(token, waitForAdmission: false); interlock.Release(); }
            long guardedAsyncBytes = GC.GetAllocatedBytesForCurrentThread() - before;
            if (round == 0) { continue; }
            baseline = Math.Min(baseline, oldBytes);
            admission = Math.Min(admission, newBytes);
            asyncBaseline = Math.Min(asyncBaseline, oldAsyncBytes);
            asyncAdmission = Math.Min(asyncAdmission, newAsyncBytes);
            guardedAdmission = Math.Min(guardedAdmission, guardedBytes);
            guardedAsyncAdmission = Math.Min(guardedAsyncAdmission, guardedAsyncBytes);
        }
        // Assert
        baseline.ShouldBe(0);
        admission.ShouldBe(0);
        asyncBaseline.ShouldBe(0);
        asyncAdmission.ShouldBe(0);
        guardedAdmission.ShouldBe(0);
        guardedAsyncAdmission.ShouldBe(0);
        _output.WriteLine($"Admission B/call: sync={admission / 1024.0:F2}, async={asyncAdmission / 1024.0:F2}, guardedSync={guardedAdmission / 1024.0:F2}, guardedAsync={guardedAsyncAdmission / 1024.0:F2}, semaphoreSync={baseline / 1024.0:F2}, semaphoreAsync={asyncBaseline / 1024.0:F2}");
    }

    [Fact(DisplayName = "Cohesion Test [Database.Transactions] - Version purge: apply callback refuses reentrancy before any work (w11 review)")]
    public async Task RunVersionPurgePass_InsideApply_ShouldRefuseBeforePruning()
    {
        // Arrange: the escape token makes the base failure terminate instead of hanging.
        using var storage = CoordinatorStorage.Create();
        using var escape = new CancellationTokenSource(TimeSpan.FromSeconds(1));
        int pruned = 0;
        await using var coordinator = new TransactionCoordinator(storage, storage.Log, storage.Records,
            (_, _) => { pruned++; return new ValueTask<long>(0); });
        var context = await coordinator.BeginAsync(IsolationLevel.ReadCommitted, CancellationToken.None);

        // Act / Assert: reentrancy is a storage-transaction refusal, never cancellation.
        await coordinator.ApplyStatementAsync(context, _ =>
        {
            Should.Throw<StorageTransactionException>(() => coordinator.RunVersionPurgePass(escape.Token));
            pruned.ShouldBe(0);
            return true;
        }, CancellationToken.None);
        await coordinator.RollbackAsync(context, CancellationToken.None);
        coordinator.RunVersionPurgePass(CancellationToken.None).ShouldBe(0);
        pruned.ShouldBe(1);
    }

    [Fact(DisplayName = "Cohesion Test [Database.Transactions] - Index prune: its callback owns the apply gate and reports reclaimed work (#1195)")]
    public async Task RunVersionPurgePass_IndexCallback_ShouldHoldApplyGateAndReportItsWork()
    {
        // Arrange
        using var storage = CoordinatorStorage.Create();
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        using var applied = new ManualResetEventSlim();
        await using var coordinator = new TransactionCoordinator(storage, storage.Log, storage.Records, (bound, token) =>
        {
            bound.Value.ShouldBeGreaterThan(0UL);
            entered.Set();
            release.Wait(TimeSpan.FromSeconds(30), token).ShouldBeTrue("hang guard only");
            return new ValueTask<long>(7);
        });
        var context = await coordinator.BeginAsync(IsolationLevel.Snapshot);
        // Act
        var purge = DedicatedTestTask.Run(() => coordinator.RunVersionPurgePass(CancellationToken.None));
        bool blocked;
        Task<bool>? statement = null;
        try
        {
            entered.Wait(TimeSpan.FromSeconds(30)).ShouldBeTrue("hang guard only");
            statement = coordinator.ApplyStatementAsync(context, _ => { applied.Set(); return true; }).AsTask();
            blocked = !statement.IsCompleted && !applied.IsSet;
        }
        finally { release.Set(); }
        // Assert: thirty-second waits above and below are hang guards only.
        (await purge.WaitAsync(TimeSpan.FromSeconds(30))).ShouldBe(7);
        (await statement!.WaitAsync(TimeSpan.FromSeconds(30))).ShouldBeTrue();
        blocked.ShouldBeTrue();
        await coordinator.RollbackAsync(context);
    }

    [Theory(DisplayName = "Cohesion Test [Database.Transactions] - Index prune: failure and cancellation retain identity and release the gate (#1195)")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RunVersionPurgePass_IndexCallbackFails_ShouldPreserveIdentityAndReleaseGate(bool cancellation)
    {
        // Arrange
        using var storage = CoordinatorStorage.Create();
        var failure = cancellation ? (Exception)new OperationCanceledException("provider cancelled") : new IOException("index read failed");
        await using var coordinator = new TransactionCoordinator(storage, storage.Log, storage.Records,
            (_, _) => ValueTask.FromException<long>(failure));
        Exception? observed = null;
        // Act
        try { coordinator.RunVersionPurgePass(CancellationToken.None); }
        catch (Exception exception) { observed = exception; }
        // Assert: the wait is a hang guard for the subsequent gate admission.
        observed.ShouldBeSameAs(failure);
        var context = await coordinator.BeginAsync(IsolationLevel.Snapshot);
        (await coordinator.ApplyStatementAsync(context, _ => true).AsTask().WaitAsync(TimeSpan.FromSeconds(30))).ShouldBeTrue();
        await coordinator.RollbackAsync(context);
    }

    [Fact(DisplayName = "Cohesion Test [Database.Transactions] - Checkpoint: readers begin, pin and end while the physical checkpoint owns both gates (#1269)")]
    public async Task BeginAsync_CheckpointBlockedInDataIo_ShouldAllowReadersAndPreserveLateWriter()
    {
        // Arrange: stop in real checkpoint I/O, after its sequence floor was captured.
        using var data = new CheckpointDataStream();
        using var storage = CoordinatorStorage.Create(data);
        await using var coordinator = new TransactionCoordinator(storage, storage.Log, storage.Records);
        PageId pageId;
        using (var bracket = storage.BeginTransaction())
        {
            using (var page = storage.AllocatePageForWrite(bracket, PageType.Data))
            {
                pageId = page.Id;
            }
            bracket.Commit();
        }
        long before = storage.ReserveTransactionSequence();
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        data.BeforeWrite = () =>
        {
            entered.Set();
            release.Wait(TimeSpan.FromSeconds(20)).ShouldBeTrue();
        };
        var checkpoint = DedicatedTestTask.Run(coordinator.Checkpoint);
        TransactionContext? late = null;
        int completed = 0;
        try
        {
            entered.Wait(TimeSpan.FromSeconds(10)).ShouldBeTrue();

            // Act: reservation cannot take the physical checkpoint's transaction lock;
            // begin/commit/rollback cannot take its lifecycle gate, nor pins its pool lock.
            await DedicatedTestTask.Run(async () =>
            {
                using var canceled = new CancellationTokenSource();
                canceled.Cancel();
                await Should.ThrowAsync<OperationCanceledException>(async () =>
                    await coordinator.BeginAsync(IsolationLevel.Snapshot, canceled.Token));
                for (int i = 0; i < 32; i++)
                {
                    var reader = await coordinator.BeginAsync(IsolationLevel.Snapshot, CancellationToken.None);
                    using (var page = storage.PageManager.GetPage(pageId))
                    {
                        page.Page.Type.ShouldBe(PageType.Data);
                    }
                    if ((i & 1) == 0)
                    {
                        await coordinator.CommitAsync(reader, CancellationToken.None);
                    }
                    else
                    {
                        await coordinator.RollbackAsync(reader, CancellationToken.None);
                    }
                    completed++;
                }
                late = await coordinator.BeginAsync(IsolationLevel.Snapshot, CancellationToken.None);
            }).WaitAsync(TimeSpan.FromSeconds(10));
        }
        finally
        {
            data.BeforeWrite = null;
            release.Set();
            await checkpoint.WaitAsync(TimeSpan.FromSeconds(10));
        }

        // Assert: a reservation above that captured floor can later stamp only with fresh
        // enrollment in the retained WAL, and recovery still classifies it as uncommitted.
        completed.ShouldBe(32);
        var promoted = late.ShouldNotBeNull();
        promoted.Sequence.Value.ShouldBeGreaterThan((ulong)before);
        storage.Log.ReadAll().ShouldNotContain(record => record.Type == JournalRecordType.BeginTransaction);
        await coordinator.ApplyStatementAsync(promoted, bracket =>
            storage.Insert(bracket, Stamped(promoted.Sequence, TransactionSequence.None)).SlotIndex);
        var images = storage.CaptureImages();
        using var reopened = CoordinatorStorage.Open(images.Data, images.Journal);
        await using var recovered = new TransactionCoordinator(reopened, reopened.Log, reopened.Records);
        var plan = recovered.AnalyzeAndScrub();
        plan.Aborted.ShouldContain(promoted.Sequence);
        RecordCount(reopened).ShouldBe(0);
        var next = await recovered.BeginAsync(IsolationLevel.Snapshot, CancellationToken.None);
        next.Sequence.ShouldBeGreaterThan(promoted.Sequence);
        await recovered.CommitAsync(next, CancellationToken.None);
    }

    [Fact(DisplayName = "Cohesion Test [Database.Transactions] - Lazy enrollment: cancellation before admission leaves a reader and a failed callback releases the gate (#1280)")]
    public async Task ApplyStatement_CancellationAndCallbackFailure_ShouldReleaseAdmissionAndKeepClassification()
    {
        // Arrange
        using var storage = CoordinatorStorage.Create();
        await using var coordinator = new TransactionCoordinator(storage, storage.Log, storage.Records);
        long lsn = storage.Log.LastLsn;
        long length = storage.JournalLength;
        var reader = await coordinator.BeginAsync(IsolationLevel.Snapshot, CancellationToken.None);
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();

        // Act: cancellation takes no admission and never enrolls the reader.
        await Should.ThrowAsync<OperationCanceledException>(async () =>
            await coordinator.ApplyStatementAsync(reader, _ => 0, cancellationToken: canceled.Token));
        await coordinator.CommitAsync(reader, CancellationToken.None);

        // Assert
        storage.Log.LastLsn.ShouldBe(lsn);
        storage.JournalLength.ShouldBe(length);

        // Act: an admitted callback is conservatively a writer even when it throws before writing.
        var failed = await coordinator.BeginAsync(IsolationLevel.Snapshot, CancellationToken.None);
        await Should.ThrowAsync<InvalidOperationException>(async () =>
            await coordinator.ApplyStatementAsync<int>(failed, (Func<StorageTransaction, int>)(_ =>
                throw new InvalidOperationException("Injected apply failure.")), cancellationToken: CancellationToken.None));
        await coordinator.RollbackAsync(failed, CancellationToken.None).AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        var next = await coordinator.BeginAsync(IsolationLevel.Snapshot, CancellationToken.None);
        await coordinator.ApplyStatementAsync(next, _ => 0, cancellationToken: CancellationToken.None)
            .AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        await coordinator.CommitAsync(next, CancellationToken.None);

        // Assert: neither failure kept the semaphore or admission, and recovery sees both outcomes.
        var plan = TransactionRecovery.Analyze(storage.Log);
        plan.Aborted.ShouldContain(failed.Sequence);
        plan.Committed.ShouldContain(next.Sequence);
        plan.Committed.ShouldNotContain(reader.Sequence);
        plan.Aborted.ShouldNotContain(reader.Sequence);
    }

    [Fact(DisplayName = "Cohesion Test [Database.Transactions] - Lazy enrollment: repeated checkpoint races classify each promoted reader and release admission (#1280)")]
    public async Task ApplyStatement_CheckpointRacesWithFirstWrite_ShouldEnrollEveryPromotedReader()
    {
        // Arrange: each round holds a checkpoint while a reader queues its first apply.
        using var storage = CoordinatorStorage.Create();
        await using var coordinator = new TransactionCoordinator(storage, storage.Log, storage.Records);
        var clock = Stopwatch.StartNew();
        int queuedPromotions = 0;
        int completedCheckpoints = 0;

        // Act: run for a minimum interval and until counters prove the race was exercised.
        do
        {
            using var entered = new ManualResetEventSlim();
            using var release = new ManualResetEventSlim();
            coordinator.BeforeCheckpoint = _ =>
            {
                entered.Set();
                release.Wait(TimeSpan.FromSeconds(10)).ShouldBeTrue();
            };
            var checkpoint = DedicatedTestTask.Run(coordinator.Checkpoint);
            TransactionContext? reader = null;
            Task<int>? applying = null;
            try
            {
                entered.Wait(TimeSpan.FromSeconds(5)).ShouldBeTrue();
                reader = await coordinator.BeginAsync(IsolationLevel.Snapshot, cancellationToken: CancellationToken.None);
                var promoted = reader;
                applying = coordinator.ApplyStatementAsync(promoted, bracket =>
                {
                    var location = storage.Insert(bracket, Stamped(promoted.Sequence, TransactionSequence.None));
                    coordinator.VersionStore.RecordCreated(promoted.Sequence, location.PageId, location.SlotIndex);
                    return location.SlotIndex;
                }, cancellationToken: CancellationToken.None).AsTask();
                if (!applying.IsCompleted)
                {
                    queuedPromotions++;
                }
            }
            finally
            {
                release.Set();
                await checkpoint.WaitAsync(TimeSpan.FromSeconds(5));
                completedCheckpoints++;
                if (applying is not null)
                {
                    await applying.WaitAsync(TimeSpan.FromSeconds(5));
                }
                coordinator.BeforeCheckpoint = null;
            }

            var writer = reader.ShouldNotBeNull();
            storage.Log.ReadAll().Where(record => record.TransactionSequence == (long)writer.Sequence.Value)
                .Select(record => record.Type).ShouldBe([JournalRecordType.BeginTransaction]);
            if ((queuedPromotions & 1) == 0)
            {
                await coordinator.CommitAsync(writer, CancellationToken.None);
                TransactionRecovery.Analyze(storage.Log).Committed.ShouldContain(writer.Sequence);
            }
            else
            {
                await coordinator.RollbackAsync(writer, CancellationToken.None);
                TransactionRecovery.Analyze(storage.Log).Aborted.ShouldContain(writer.Sequence);
            }
        }
        while ((clock.Elapsed < TimeSpan.FromMilliseconds(100) || queuedPromotions < 16)
            && clock.Elapsed < TimeSpan.FromSeconds(10));

        // Assert: both the checkpoint and promotion completed on every exercised overlap.
        queuedPromotions.ShouldBeGreaterThanOrEqualTo(16);
        completedCheckpoints.ShouldBe(queuedPromotions);
        coordinator.GetOpenContexts().ShouldBeEmpty();
        coordinator.Checkpoint();
        storage.CheckpointActiveTransactions.ShouldBeEmpty();
    }

    [Theory(DisplayName = "Cohesion Test [Database.Transactions] - Coordinator: lifecycle appends cannot interleave with checkpoint truncation")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Checkpoint_ConcurrentLifecycleAppend_ShouldPreserveClassification(bool commit)
    {
        using var storage = CoordinatorStorage.Create();
        await using var coordinator = new TransactionCoordinator(storage, storage.Log, storage.Records);
        var active = await coordinator.BeginAsync(IsolationLevel.Snapshot);

        // A writer: only a transaction that applied a statement is listed (#1242).
        await coordinator.ApplyStatementAsync(active, bracket =>
            storage.Insert(bracket, Stamped(active.Sequence, TransactionSequence.None)).SlotIndex);
        using var checkpointEntered = new ManualResetEventSlim();
        using var releaseCheckpoint = new ManualResetEventSlim();
        using var lifecycleStarted = new ManualResetEventSlim();

        coordinator.BeforeCheckpoint = sequences =>
        {
            sequences.ShouldContain((long)active.Sequence.Value);
            checkpointEntered.Set();
            releaseCheckpoint.Wait(TimeSpan.FromSeconds(10)).ShouldBeTrue();
        };

        var checkpoint = Task.Factory.StartNew(coordinator.Checkpoint,
            CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        Task<TransactionContext>? lifecycle = null;

        try
        {
            checkpointEntered.Wait(TimeSpan.FromSeconds(5)).ShouldBeTrue();
            // For enrollment, the allocator signals that the operation reached the
            // coordinator while its journal gate is held by the checkpoint.
            coordinator.SequenceReserved = () => lifecycleStarted.Set();
            lifecycle = Task.Factory.StartNew(() =>
            {
                if (commit)
                {
                    lifecycleStarted.Set();
                    coordinator.CommitAsync(active).AsTask().GetAwaiter().GetResult();
                    return active;
                }

                var promoted = coordinator.BeginAsync(IsolationLevel.Snapshot).AsTask().GetAwaiter().GetResult();
                coordinator.ApplyStatementAsync(promoted, bracket =>
                    storage.Insert(bracket, Stamped(promoted.Sequence, TransactionSequence.None)).SlotIndex)
                    .AsTask().GetAwaiter().GetResult();
                return promoted;
            }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);

            lifecycleStarted.Wait(TimeSpan.FromSeconds(5)).ShouldBeTrue();
            // An append that escaped the gate could complete here, between the
            // captured active list and the actual WAL truncation.
            (await Task.WhenAny(lifecycle, Task.Delay(150))).ShouldNotBeSameAs(lifecycle);
        }
        finally
        {
            releaseCheckpoint.Set();
            await checkpoint.WaitAsync(TimeSpan.FromSeconds(5));
            if (lifecycle is not null)
            {
                await lifecycle.WaitAsync(TimeSpan.FromSeconds(5));
            }
        }

        var appended = await lifecycle!;
        var plan = TransactionRecovery.Analyze(storage.Log);
        if (commit)
        {
            // The commit must follow the checkpoint carrying its active begin;
            // truncating it instead would resurrect an aborted classification.
            plan.Committed.ShouldContain(active.Sequence);
            plan.Aborted.ShouldNotContain(active.Sequence);
        }
        else
        {
            // The old writer is represented by the checkpoint; the new begin
            // survives after it. Both still require crash-time undo.
            plan.Aborted.ShouldContain(active.Sequence);
            plan.Aborted.ShouldContain(appended.Sequence);
        }
    }

    [Fact(DisplayName = "Cohesion Test [Database.Transactions] - Coordinator: recovery scrubs before truncation and anchors the idle prune bound")]
    public async Task Recovery_ReopenedJournal_ShouldScrubBeforeCheckpointAndPruneOldTombstones()
    {
        using var original = CoordinatorStorage.Create();
        var committed = new TransactionSequence((ulong)original.ReserveTransactionSequence());
        var deleted = new TransactionSequence((ulong)original.ReserveTransactionSequence());
        var aborted = new TransactionSequence((ulong)original.ReserveTransactionSequence());
        original.Log.AppendBegin((long)committed.Value);
        original.Log.AppendCommit((long)committed.Value);
        original.Log.AppendBegin((long)deleted.Value);
        original.Log.AppendCommit((long)deleted.Value);
        original.Log.AppendBegin((long)aborted.Value);

        (PageId PageId, int SlotIndex) retained;
        using (var statement = original.BeginTransaction())
        {
            original.Insert(statement, Stamped(committed, deleted));
            original.Insert(statement, Stamped(aborted, TransactionSequence.None));
            retained = original.Insert(statement, Stamped(committed, aborted));
            statement.Commit();
        }

        // Capture without clean disposal: all statement page changes survived,
        // while the logical aborted writer has no durable commit record.
        var images = original.CaptureImages();
        using var reopened = CoordinatorStorage.Open(images.Data, images.Journal);
        await using var coordinator = new TransactionCoordinator(reopened, reopened.Log, reopened.Records);
        coordinator.Manager.OldestActive.Value.ShouldBeLessThan(deleted.Value);

        var plan = coordinator.AnalyzeAndScrub();

        plan.Aborted.ShouldContain(aborted);
        RecordCount(reopened).ShouldBe(2);
        BinaryPrimitives.ReadUInt64LittleEndian(reopened.Read(retained.PageId, retained.SlotIndex).Span.Slice(8, 8))
            .ShouldBe(0UL);
        coordinator.VersionStore.TrackedVersionCount.ShouldBe(1);
        // The caller still needs these lifecycle records to scrub its indexes.
        TransactionRecovery.Analyze(reopened.Log).Aborted.ShouldContain(aborted);

        // No post-restart logical begin has advanced the fresh manager. The
        // recovered sequence floor alone makes this old tombstone reclaimable.
        coordinator.RunVersionPurgePass(CancellationToken.None).ShouldBe(1);
        RecordCount(reopened).ShouldBe(1);

        coordinator.CompleteRecovery();

        reopened.Log.ReadAll().Select(record => record.Type).ShouldBe([JournalRecordType.Checkpoint]);
        TransactionRecovery.Analyze(reopened.Log).Aborted.ShouldBeEmpty();
    }

    [Fact(DisplayName = "Cohesion Test [Database.Transactions] - Coordinator: an older snapshot minimum pins a committed deleter")]
    public async Task Prune_CommittedDeleterBelowOldestActive_ShouldKeepPinnedSnapshotVersion()
    {
        using var storage = CoordinatorStorage.Create();
        (PageId PageId, int SlotIndex) location;
        using (var setup = storage.BeginTransaction())
        {
            location = storage.Insert(setup, Stamped(TransactionSequence.None, TransactionSequence.None));
            setup.Commit();
        }

        await using var coordinator = new TransactionCoordinator(storage, storage.Log, storage.Records);
        var deleter = await coordinator.BeginAsync(IsolationLevel.Snapshot);
        var pinned = await coordinator.BeginAsync(IsolationLevel.Snapshot);
        pinned.Snapshot.Minimum.ShouldBe(deleter.Sequence);

        await coordinator.ApplyStatementAsync(deleter, bracket =>
        {
            storage.Update(bracket, location.PageId, location.SlotIndex,
                Stamped(TransactionSequence.None, deleter.Sequence));
            coordinator.VersionStore.RecordTombstoned(deleter.Sequence, location.PageId, location.SlotIndex);
            return 0;
        });
        await coordinator.CommitAsync(deleter);

        coordinator.Manager.OldestActive.ShouldBe(pinned.Sequence);
        coordinator.Manager.OldestActive.Value.ShouldBeGreaterThan(deleter.Sequence.Value);
        coordinator.RunVersionPurgePass(CancellationToken.None).ShouldBe(0);
        (await coordinator.VersionStore.GetVisibleVersionAsync(0,
            storage.PackLocation(location.PageId, location.SlotIndex), pinned.Snapshot)).ShouldNotBeNull();

        await coordinator.CommitAsync(pinned);

        coordinator.RunVersionPurgePass(CancellationToken.None).ShouldBe(1);
        RecordCount(storage).ShouldBe(0);
    }

    /// <summary>
    /// A rollback that runs while a statement of the transaction is inside the apply gate waits for
    /// that statement, undoes its bracket, and refuses every later one, so no record keeps the
    /// rolled-back sequence. Before the #1225 review the rollback took the empty ledger at once,
    /// the bracket landed afterwards, and once a checkpoint truncated the abort record, recovery
    /// read the tombstone as committed and the purge reclaimed a record that was never deleted.
    /// </summary>
    [Fact(DisplayName = "Cohesion Test [Database.Transactions] - Coordinator: a rollback under a running statement undoes it and refuses later brackets")]
    public async Task RollbackAsync_WhileStatementApplies_ShouldUndoItAndSurviveCheckpointAndReopen()
    {
        // Arrange: a committed record, and a statement of the writer held inside the apply gate
        // before it tombstones the record.
        using var storage = CoordinatorStorage.Create();
        (PageId PageId, int SlotIndex) location;
        using (var setup = storage.BeginTransaction())
        {
            location = storage.Insert(setup, Stamped(TransactionSequence.None, TransactionSequence.None));
            setup.Commit();
        }

        var coordinator = new TransactionCoordinator(storage, storage.Log, storage.Records);
        var writer = await coordinator.BeginAsync(IsolationLevel.Snapshot);
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var running = Task.Factory.StartNew(() => coordinator.ApplyStatementAsync(writer, bracket =>
        {
            entered.Set();
            release.Wait(TimeSpan.FromSeconds(10)).ShouldBeTrue();
            storage.Update(bracket, location.PageId, location.SlotIndex, Stamped(TransactionSequence.None, writer.Sequence));
            coordinator.VersionStore.RecordTombstoned(writer.Sequence, location.PageId, location.SlotIndex);
            return 0;
        }).AsTask().GetAwaiter().GetResult(), CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        entered.Wait(TimeSpan.FromSeconds(10)).ShouldBeTrue();
        bool laterApplied = false;

        // Act
        var rollingBack = coordinator.RollbackAsync(writer).AsTask();
        bool waitedForStatement = !rollingBack.IsCompleted;
        release.Set();
        await running.WaitAsync(TimeSpan.FromSeconds(10));
        await rollingBack.WaitAsync(TimeSpan.FromSeconds(10));
        var refused = await Should.ThrowAsync<TransactionAbortedException>(async () =>
            await coordinator.ApplyStatementAsync(writer, bracket =>
            {
                laterApplied = true;
                return 0;
            }));
        int pairedAfterEnd = coordinator.PairedTransactionCount;
        coordinator.Checkpoint();
        var images = storage.CaptureImages();
        await coordinator.DisposeAsync();
        using var reopened = CoordinatorStorage.Open(images.Data, images.Journal);
        await using var recovered = new TransactionCoordinator(reopened, reopened.Log, reopened.Records);
        recovered.AnalyzeAndScrub();
        recovered.CompleteRecovery();
        long pruned = recovered.RunVersionPurgePass(CancellationToken.None);

        // Assert
        waitedForStatement.ShouldBeTrue();
        writer.State.ShouldBe(TransactionState.RolledBack);
        refused.Message.ShouldContain("was not applied", Case.Sensitive);
        laterApplied.ShouldBeFalse();
        pairedAfterEnd.ShouldBe(0);
        RecordVersionStamp.ReadStamps(storage.Read(location.PageId, location.SlotIndex).Span).Deleter.ShouldBe(TransactionSequence.None);
        pruned.ShouldBe(0);
        RecordCount(reopened).ShouldBe(1);
        RecordVersionStamp.ReadStamps(reopened.Read(location.PageId, location.SlotIndex).Span).Deleter.ShouldBe(TransactionSequence.None);
    }

    /// <summary>
    /// A commit that starts while a statement of the transaction is inside the apply gate waits for
    /// it, so the commit record follows every bracket stamped with the sequence, and a statement
    /// after the commit is refused.
    /// </summary>
    [Fact(DisplayName = "Cohesion Test [Database.Transactions] - Coordinator: a commit waits for a running statement and refuses later brackets")]
    public async Task CommitAsync_WhileStatementApplies_ShouldWaitForItAndRefuseLaterBrackets()
    {
        // Arrange
        using var storage = CoordinatorStorage.Create();
        await using var coordinator = new TransactionCoordinator(storage, storage.Log, storage.Records);
        var writer = await coordinator.BeginAsync(IsolationLevel.Snapshot);
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        (PageId PageId, int SlotIndex) location = default;
        var running = Task.Factory.StartNew(() => coordinator.ApplyStatementAsync(writer, bracket =>
        {
            entered.Set();
            release.Wait(TimeSpan.FromSeconds(10)).ShouldBeTrue();
            location = storage.Insert(bracket, Stamped(writer.Sequence, TransactionSequence.None));
            coordinator.VersionStore.RecordCreated(writer.Sequence, location.PageId, location.SlotIndex);
            return 0;
        }).AsTask().GetAwaiter().GetResult(), CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        entered.Wait(TimeSpan.FromSeconds(10)).ShouldBeTrue();

        // Act
        var committing = coordinator.CommitAsync(writer).AsTask();
        bool waitedForStatement = !committing.IsCompleted;
        release.Set();
        await running.WaitAsync(TimeSpan.FromSeconds(10));
        await committing.WaitAsync(TimeSpan.FromSeconds(10));
        await Should.ThrowAsync<TransactionAbortedException>(async () =>
            await coordinator.ApplyStatementAsync(writer, bracket => 0));
        var reader = await coordinator.BeginAsync(IsolationLevel.Snapshot);

        // Assert
        waitedForStatement.ShouldBeTrue();
        writer.State.ShouldBe(TransactionState.Committed);
        TransactionRecovery.Analyze(storage.Log).Committed.ShouldContain(writer.Sequence);
        (await coordinator.VersionStore.GetVisibleVersionAsync(0,
            storage.PackLocation(location.PageId, location.SlotIndex), reader.Snapshot)).ShouldNotBeNull();
        await coordinator.RollbackAsync(reader);
    }

    /// <summary>
    /// Only a transaction that applied a statement can have stamped a row version, so only
    /// writers are listed by a checkpoint and its anchor (#1242); readers, however many, cost
    /// the anchor nothing.
    /// </summary>
    [Fact(DisplayName = "Cohesion Test [Database.Transactions] - Coordinator checkpoint: only writers are anchored, not readers")]
    public async Task Checkpoint_ReadersAndWriters_ShouldAnchorOnlyTheWriters()
    {
        // Arrange
        using var storage = CoordinatorStorage.Create();
        await using var coordinator = new TransactionCoordinator(storage, storage.Log, storage.Records);
        var reader = await coordinator.BeginAsync(IsolationLevel.Snapshot);
        var writer = await coordinator.BeginAsync(IsolationLevel.Snapshot);
        var secondReader = await coordinator.BeginAsync(IsolationLevel.Snapshot);
        await coordinator.ApplyStatementAsync(writer, bracket =>
            storage.Insert(bracket, Stamped(writer.Sequence, TransactionSequence.None)).SlotIndex);
        long[] listed = [];
        coordinator.BeforeCheckpoint = sequences => listed = sequences;

        // Act
        coordinator.Checkpoint();

        // Assert
        listed.ShouldBe([(long)writer.Sequence.Value]);
        storage.CheckpointActiveTransactions.ShouldBe([(long)writer.Sequence.Value]);
        var checkpointRecord = storage.Log.ReadAll().Single();
        checkpointRecord.Payload.Length.ShouldBe(sizeof(long));
        await coordinator.CommitAsync(writer);
        await coordinator.CommitAsync(reader);
        await coordinator.CommitAsync(secondReader);
    }

    /// <summary>
    /// A reader has no begin record, and the checkpoint does not list it.
    /// When it then applies a statement, the coordinator appends its first begin record
    /// before the statement's bracket, so a crash before its commit still finds it unproven and
    /// scrubs what it wrote. Without that, recovery would read its version as committed.
    /// </summary>
    [Fact(DisplayName = "Cohesion Test [Database.Transactions] - Coordinator checkpoint: a reader that writes after a checkpoint is enrolled and scrubbed after a crash")]
    public async Task Checkpoint_ReaderWritesAfterTheCheckpoint_ShouldEnrollAndScrubAfterACrash()
    {
        // Arrange: a reader across a checkpoint, with no begin record before or after it.
        using var storage = CoordinatorStorage.Create();
        var coordinator = new TransactionCoordinator(storage, storage.Log, storage.Records);
        var late = await coordinator.BeginAsync(IsolationLevel.Snapshot);
        storage.Log.ReadAll().ShouldNotContain(record => record.TransactionSequence == (long)late.Sequence.Value);
        coordinator.Checkpoint();
        bool namedAfterTheCheckpoint = storage.Log.ReadAll().Any(record => record.TransactionSequence == (long)late.Sequence.Value);

        // Act: it writes, then the process stops before it commits.
        await coordinator.ApplyStatementAsync(late, bracket =>
            storage.Insert(bracket, Stamped(late.Sequence, TransactionSequence.None)).SlotIndex);
        var announced = storage.Log.ReadAll()
            .Where(record => record.TransactionSequence == (long)late.Sequence.Value)
            .Select(record => record.Type)
            .ToArray();
        var images = storage.CaptureImages();
        using var reopened = CoordinatorStorage.Open(images.Data, images.Journal);
        await using var recovered = new TransactionCoordinator(reopened, reopened.Log, reopened.Records);
        var plan = recovered.AnalyzeAndScrub();
        recovered.CompleteRecovery();

        // Assert: the uncommitted version is gone — the loss of enrollment would leave it
        // readable as committed — and the begin record is what named the transaction.
        plan.Aborted.ShouldContain(late.Sequence);
        RecordCount(reopened).ShouldBe(0);
        namedAfterTheCheckpoint.ShouldBeFalse();
        announced.ShouldBe([JournalRecordType.BeginTransaction]);
    }

    /// <summary>
    /// A statement holds the apply gate. The bounded checkpoint does not wait for it: it returns
    /// at once, and the statement runs the checkpoint as it ends, before it releases the gate, so
    /// a long statement in one database never parks the engine's checkpoint worker (#1254 review).
    /// </summary>
    [Fact(DisplayName = "Cohesion Test [Database.Transactions] - Coordinator checkpoint: a held gate defers the checkpoint to the statement's end")]
    public async Task TryCheckpoint_GateHeldByAStatement_ShouldDeferTheCheckpointToTheStatementsEnd()
    {
        // Arrange: a statement that holds the gate until the test releases it.
        using var storage = CoordinatorStorage.Create();
        await using var coordinator = new TransactionCoordinator(storage, storage.Log, storage.Records);
        int checkpoints = 0;
        coordinator.BeforeCheckpoint = _ => checkpoints++;
        var writer = await coordinator.BeginAsync(IsolationLevel.Snapshot);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var statement = coordinator.ApplyStatementAsync<int>(writer, async bracket =>
        {
            int slot = storage.Insert(bracket, Stamped(writer.Sequence, TransactionSequence.None)).SlotIndex;
            entered.SetResult();
            await release.Task;
            return slot;
        }, durable: false).AsTask();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));

        // Act
        bool ranWhileHeld = coordinator.TryCheckpoint(TimeSpan.Zero, CancellationToken.None);
        int checkpointsWhileHeld = checkpoints;
        release.SetResult();
        await statement.WaitAsync(TimeSpan.FromSeconds(10));
        int checkpointsAtTheStatementsEnd = checkpoints;
        bool ranWhenFree = coordinator.TryCheckpoint(TimeSpan.Zero, CancellationToken.None);

        // Assert: deferred, run once by the statement's end, and run directly once the gate is free.
        ranWhileHeld.ShouldBeFalse();
        checkpointsWhileHeld.ShouldBe(0);
        checkpointsAtTheStatementsEnd.ShouldBe(1);
        ranWhenFree.ShouldBeTrue();
        checkpoints.ShouldBe(2);
        storage.CheckpointActiveTransactions.ShouldBe([(long)writer.Sequence.Value]);
        await coordinator.CommitAsync(writer);
    }

    /// <summary>
    /// The checkpoint a statement ran for a deferred request fails. The statement does not fail
    /// for it: its own outcome was decided. The failure is thrown by the next bounded checkpoint,
    /// so the engine's checkpoint worker learns of it.
    /// </summary>
    [Theory(DisplayName = "Cohesion Test [Database.Transactions] - Coordinator checkpoint: a deferred checkpoint's failure reaches the next checkpoint call, not the statement")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TryCheckpoint_DeferredCheckpointFails_ShouldThrowFromTheNextCallNotTheStatement(bool foreignStorage)
    {
        // Arrange
        using var storage = CoordinatorStorage.Create();
        await using var coordinator = new TransactionCoordinator(storage, storage.Log, storage.Records);
        Exception failure = foreignStorage
            ? new StorageTransactionException(StorageTransactionErrorCode.ForeignStorage, "Injected foreign storage checkpoint failure.")
            : new InvalidOperationException("Injected checkpoint failure.");
        var writer = await coordinator.BeginAsync(IsolationLevel.Snapshot);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var statement = coordinator.ApplyStatementAsync<int>(writer, async bracket =>
        {
            int slot = storage.Insert(bracket, Stamped(writer.Sequence, TransactionSequence.None)).SlotIndex;
            entered.SetResult();
            await release.Task;
            return slot;
        }, durable: false).AsTask();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        coordinator.TryCheckpoint(TimeSpan.Zero, CancellationToken.None).ShouldBeFalse();
        coordinator.BeforeCheckpoint = _ => throw failure;

        // Act
        release.SetResult();
        var statementError = await Record.ExceptionAsync(() => statement.WaitAsync(TimeSpan.FromSeconds(10)));
        coordinator.BeforeCheckpoint = null;
        var reported = Record.Exception(() => coordinator.TryCheckpoint(TimeSpan.Zero, CancellationToken.None));
        bool ranAfterTheReport = coordinator.TryCheckpoint(TimeSpan.Zero, CancellationToken.None);

        // Assert
        statementError.ShouldBeNull();
        reported.ShouldBeSameAs(failure);
        ranAfterTheReport.ShouldBeTrue();
        await coordinator.CommitAsync(writer);
    }

    /// <summary>
    /// The apply gate is not reentrant. A checkpoint asked for from inside a statement apply would
    /// wait for the gate its own caller holds, forever; it is refused at once instead (#1254 review).
    /// </summary>
    [Fact(DisplayName = "Cohesion Test [Database.Transactions] - Coordinator checkpoint: a checkpoint inside a statement apply is refused, not deadlocked")]
    public async Task Checkpoint_InsideAStatementApply_ShouldBeRefusedNotDeadlocked()
    {
        // Arrange
        using var storage = CoordinatorStorage.Create();
        await using var coordinator = new TransactionCoordinator(storage, storage.Log, storage.Records);
        var writer = await coordinator.BeginAsync(IsolationLevel.Snapshot);
        Exception? direct = null;
        Exception? bounded = null;

        // Act
        await coordinator.ApplyStatementAsync<int>(writer, bracket =>
        {
            direct = Record.Exception(() => coordinator.Checkpoint());
            bounded = Record.Exception(() => coordinator.TryCheckpoint(TimeSpan.FromSeconds(1), CancellationToken.None));
            return ValueTask.FromResult(0);
        }, durable: false).AsTask().WaitAsync(TimeSpan.FromSeconds(10));
        coordinator.Checkpoint();

        // Assert: both refused inside; the same call succeeds once the statement completed.
        direct.ShouldBeOfType<StorageTransactionException>();
        bounded.ShouldBeOfType<StorageTransactionException>();
        await coordinator.CommitAsync(writer);
    }

    private static byte[] Stamped(TransactionSequence writer, TransactionSequence deleter)
    {
        var bytes = new byte[17];
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(0, 8), writer.Value);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(8, 8), deleter.Value);
        bytes[16] = 42;
        return bytes;
    }

    private static int RecordCount(Storage.Storage storage)
    {
        using var iterator = storage.GetUnitIterator();
        int count = 0;
        while (iterator.MoveNext())
        {
            count++;
        }

        return count;
    }

    // Only the record-space boundary is a test double. Pages, record iteration,
    // transactions, durability, journal replay, and truncation are real Storage; the
    // checkpoint and sequence-reservation hooks are the coordinator's own (#1257).
    private sealed class CoordinatorStorage : Storage.Storage
    {
        private readonly MemoryStream _data;
        private readonly MemoryStream _journal;

        private CoordinatorStorage(MemoryStream data, MemoryStream journal, bool reopen)
            : base(StorageModel.KeyValue, new StorageStream(new SimulatedDurableFileHandle(data)), new StorageStream(new SimulatedDurableFileHandle(journal)), new StorageStream(new MemoryStream()))
        {
            _data = data;
            _journal = journal;
            if (reopen)
            {
                OpenExisting(checkpointOnOpen: false);
            }
            else
            {
                InitializeNew((Name)"coordinator-test");
            }
        }

        internal StorageJournal Log => WriteAheadLog;

        internal static CoordinatorStorage Create(MemoryStream? data = null) => new(data ?? new MemoryStream(), new MemoryStream(), reopen: false);

        internal static CoordinatorStorage Open(byte[] data, byte[] journal)
            => new(Copy(data), Copy(journal), reopen: true);

        internal (byte[] Data, byte[] Journal) CaptureImages()
        {
            Flush();
            return (_data.ToArray(), _journal.ToArray());
        }

        internal (PageId PageId, int SlotIndex) Insert(StorageTransaction bracket, ReadOnlySpan<byte> data)
            => InsertRecord(bracket, data);

        internal ReadOnlyMemory<byte> Read(PageId pageId, int slotIndex) => ReadRecord(pageId, slotIndex);

        internal void Update(StorageTransaction bracket, PageId pageId, int slotIndex, ReadOnlySpan<byte> record)
            => UpdateRecord(bracket, pageId, slotIndex, record);

        internal void Delete(StorageTransaction bracket, PageId pageId, int slotIndex)
            => DeleteRecord(bracket, pageId, slotIndex);

        internal ulong PackLocation(PageId pageId, int slotIndex)
            => ((ulong)(long)pageId << 16) | (ushort)slotIndex;

        internal (PageId PageId, int SlotIndex) UnpackLocation(ulong location)
            => ((PageId)(long)(location >> 16), (int)(location & 0xFFFF));

        /// <summary>
        /// Gets the coordinator's record space over this storage's records. The double used to be
        /// the record space itself; both are abstract classes now, so it is split (plan C9).
        /// </summary>
        internal TransactionRecordSpace Records => _records ??= new RecordSpace(this);

        private RecordSpace? _records;

        private sealed class RecordSpace : TransactionRecordSpace
        {
            private readonly CoordinatorStorage _storage;

            internal RecordSpace(CoordinatorStorage storage)
            {
                _storage = storage;
            }

            protected override ReadOnlyMemory<byte> ReadCore(PageId pageId, int slotIndex) => _storage.Read(pageId, slotIndex);

            protected override void UpdateCore(StorageTransaction transaction, PageId pageId, int slotIndex, ReadOnlySpan<byte> record)
                => _storage.Update(transaction, pageId, slotIndex, record);

            protected override void DeleteCore(StorageTransaction transaction, PageId pageId, int slotIndex)
                => _storage.Delete(transaction, pageId, slotIndex);

            protected override ulong PackLocationCore(PageId pageId, int slotIndex) => _storage.PackLocation(pageId, slotIndex);

            protected override (PageId PageId, int SlotIndex) UnpackLocationCore(ulong location) => _storage.UnpackLocation(location);
        }

        private static MemoryStream Copy(byte[] bytes)
        {
            var stream = new MemoryStream();
            stream.Write(bytes);
            stream.Position = 0;
            return stream;
        }
    }

    private sealed class CheckpointDataStream : MemoryStream
    {
        internal Action? BeforeWrite;

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            BeforeWrite?.Invoke();
            base.Write(buffer);
        }
    }
}
