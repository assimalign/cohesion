using System;
using System.Diagnostics;
using System.Diagnostics.Tracing;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Storage;
using Assimalign.Cohesion.Database.Transactions.Internal;
using Assimalign.Cohesion.Testing;

namespace Assimalign.Cohesion.Database.Transactions.Tests;

public partial class TransactionCoordinatorRecoveryTests
{
    [Fact(DisplayName = "Cohesion Test [Transactions] - Ordinary physical acquisition preserves the caller cancellation token (#1283)")]
    public async Task WaitAsync_CallerCancelsWhileQueued_ShouldPreserveTheCallerToken()
    {
        // Arrange: force the linked offline notice to participate in physical acquisition.
        using var storage = CoordinatorStorage.Create();
        await using var coordinator = new TransactionCoordinator(storage, storage.Log, storage.Records);
        using var ceiling = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        using var caller = CancellationTokenSource.CreateLinkedTokenSource(ceiling.Token);
        var gate = TransactionCheckpointInterlock.ForStorage(storage);
        gate.Wait(ceiling.Token, drain: true);
        var waiting = gate.WaitAsync(caller.Token, "QueuedWriter").AsTask();
        try
        {
            waiting.IsCompleted.ShouldBeFalse();
            // Act
            caller.Cancel();
            OperationCanceledException? canceled = null;
            try { await waiting.WaitAsync(ceiling.Token); }
            catch (OperationCanceledException error) { canceled = error; }
            // Assert: an internal cancellation source never escapes as the caller's identity.
            canceled.ShouldNotBeNull().CancellationToken.ShouldBe(caller.Token);
            ceiling.IsCancellationRequested.ShouldBeFalse();
        }
        finally { gate.Release(allowCheckpoint: false); }
    }

    [Fact(DisplayName = "Cohesion Test [Transactions] - Offline interrupts semaphore waiters queued before a pause (#1283)")]
    public async Task WaitAsync_OfflineWhileQueuedBeforePause_ShouldWakeWithoutThePermit()
    {
        // Arrange: the writer queued while there was no pause and cannot get the permit.
        using var storage = CoordinatorStorage.Create();
        await using var coordinator = new TransactionCoordinator(storage, storage.Log, storage.Records);
        using var ceiling = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        var gate = TransactionCheckpointInterlock.ForStorage(storage);
        gate.Wait(ceiling.Token, drain: true);
        var waiting = gate.WaitAsync(ceiling.Token, "QueuedWriter").AsTask();
        try
        {
            waiting.IsCompleted.ShouldBeFalse();
            using (var bracket = storage.BeginTransaction()) { bracket.Commit(); }
            storage.CheckpointJournalSize = 1; gate.JournalSizeLimit = 2;
            gate.ObserveAdmissionPause().ShouldBeTrue();
            // Act: no physical holder release or successful checkpoint follows this fence.
            storage.TakeOffline(StorageOfflineCause.PageWriteBackFailures, "write-back failure", new IOException("test failure"));
            // Assert
            (await Should.ThrowAsync<StorageOfflineException>(() => waiting.WaitAsync(ceiling.Token))).ShouldBeSameAs(storage.OfflineError);
            ceiling.IsCancellationRequested.ShouldBeFalse();
        }
        finally
        {
            gate.Release(allowCheckpoint: false); gate.JournalSizeLimit = 0;
            try { await waiting.WaitAsync(TimeSpan.FromSeconds(45)); gate.Release(allowCheckpoint: false); }
            catch (Exception error) when (error is StorageOfflineException or OperationCanceledException) { }
        }
    }

    [Fact(DisplayName = "Cohesion Test [Transactions] - A pause interrupts guarded asynchronous physical acquisition (#1283)")]
    public async Task ApplyStatementAsync_GuardedPhysicalWaitBecomesPaused_ShouldRefuseBeforeTheHolderEnds()
    {
        // Arrange
        using var storage = CoordinatorStorage.Create();
        await using var coordinator = new TransactionCoordinator(storage, storage.Log, storage.Records);
        using var ceiling = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        var context = await coordinator.BeginAsync(IsolationLevel.ReadCommitted, ceiling.Token);
        var gate = TransactionCheckpointInterlock.ForStorage(storage);
        gate.Wait(ceiling.Token, drain: true);
        bool applied = false;
        var applying = coordinator.ApplyStatementAsync(context, _ =>
        { applied = true; return ValueTask.FromResult(0); }, cancellationToken: ceiling.Token, waitForAdmission: false).AsTask();
        try
        {
            applying.IsCompleted.ShouldBeFalse();
            using (var bracket = storage.BeginTransaction()) { bracket.Commit(); }
            storage.CheckpointJournalSize = 1; gate.JournalSizeLimit = 2;
            // Act
            gate.ObserveAdmissionPause().ShouldBeTrue();
            TransactionAdmissionCanceledException? refusal = null;
            try { await applying.WaitAsync(ceiling.Token); }
            catch (TransactionAdmissionCanceledException error) { refusal = error; }
            // Assert: the pause, rather than the guard or physical release, ends acquisition.
            refusal.ShouldNotBeNull();
            ceiling.IsCancellationRequested.ShouldBeFalse(); applied.ShouldBeFalse();
        }
        finally { gate.Release(allowCheckpoint: false); gate.JournalSizeLimit = 0; }
        coordinator.Checkpoint(ceiling.Token);
        await coordinator.RollbackAsync(context, ceiling.Token);
    }

    [Fact(DisplayName = "Cohesion Test [Transactions] - Reading pause state cannot create a pause (#1283)")]
    public async Task IsAdmissionPaused_ThresholdReached_ShouldRemainAPureRead()
    {
        // Arrange
        using var storage = CoordinatorStorage.Create();
        await using var coordinator = new TransactionCoordinator(storage, storage.Log, storage.Records);
        using var ceiling = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using (var bracket = storage.BeginTransaction()) { bracket.Commit(); }
        storage.CheckpointJournalSize = 1;
        var gate = TransactionCheckpointInterlock.ForStorage(storage);
        gate.JournalSizeLimit = 2;
        // Act / Assert: only the named observation creates an episode.
        gate.IsAdmissionPaused.ShouldBeFalse();
        gate.ObserveAdmissionPause().ShouldBeTrue();
        gate.IsAdmissionPaused.ShouldBeTrue();
        coordinator.Checkpoint(ceiling.Token);
    }

    [Fact(DisplayName = "Cohesion Test [Transactions] - Version pruning yields a paused maintenance lane (#1283)")]
    public async Task RunVersionPurgePass_PrunableVersionsDuringPause_ShouldYieldWithoutParking()
    {
        // Arrange: keep a real committed tombstone eligible for pruning.
        using var storage = CoordinatorStorage.Create();
        await using var coordinator = new TransactionCoordinator(storage, storage.Log, storage.Records);
        using var ceiling = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        var writer = await coordinator.BeginAsync(IsolationLevel.ReadCommitted, ceiling.Token);
        var location = await coordinator.ApplyStatementAsync(writer, bracket =>
        {
            var row = storage.Insert(bracket, Stamped(writer.Sequence, TransactionSequence.None));
            coordinator.VersionStore.RecordCreated(writer.Sequence, row.PageId, row.SlotIndex);
            return row;
        }, ceiling.Token);
        await coordinator.CommitAsync(writer, ceiling.Token);
        var deleter = await coordinator.BeginAsync(IsolationLevel.ReadCommitted, ceiling.Token);
        await coordinator.ApplyStatementAsync(deleter, bracket =>
        {
            storage.Update(bracket, location.PageId, location.SlotIndex, Stamped(writer.Sequence, deleter.Sequence));
            coordinator.VersionStore.RecordTombstoned(deleter.Sequence, location.PageId, location.SlotIndex);
            return 0;
        }, ceiling.Token);
        await coordinator.CommitAsync(deleter, ceiling.Token);
        var gate = TransactionCheckpointInterlock.ForStorage(storage);
        storage.CheckpointJournalSize = 1;
        gate.JournalSizeLimit = 2;
        gate.ObserveAdmissionPause().ShouldBeTrue();
        var pruning = Task.Run(() => coordinator.RunVersionPurgePass(ceiling.Token), ceiling.Token);
        try
        {
            // Act / Assert: the lane returns while admission remains paused.
            (await pruning.WaitAsync(ceiling.Token)).ShouldBe(0);
            gate.IsAdmissionPaused.ShouldBeTrue();
        }
        finally
        {
            gate.JournalSizeLimit = 0;
            await pruning.WaitAsync(TimeSpan.FromSeconds(45));
        }
        coordinator.Checkpoint(ceiling.Token);
        coordinator.RunVersionPurgePass(ceiling.Token).ShouldBe(1);
    }

    [Fact(DisplayName = "Cohesion Test [Transactions] - A pending pause reports worker checkpoint failure and pairs its episode (#1283)")]
    public async Task TryCheckpoint_PendingPauseLaterFails_ShouldReportTheSameEpisode()
    {
        // Arrange
        using var storage = CoordinatorStorage.Create();
        await using var coordinator = new TransactionCoordinator(storage, storage.Log, storage.Records);
        using var ceiling = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var recorder = new EventSourceRecorder(TransactionEventSource.Log, EventLevel.Verbose);
        var gate = TransactionCheckpointInterlock.ForStorage(storage);
        storage.CheckpointJournalSize = 1;
        gate.JournalSizeLimit = 2;
        using (var outside = storage.BeginTransaction())
        {
            gate.ObserveAdmissionPause().ShouldBeTrue();
            outside.Rollback();
        }
        coordinator.BeforeCheckpoint = _ => throw new IOException("worker checkpoint failed");
        // Act: a worker-style attempt fails after the pending pause has been published.
        Should.Throw<IOException>(() => coordinator.TryCheckpoint(TimeSpan.Zero, ceiling.Token));
        var paused = recorder.Events.Single(e => e.EventId == 23);
        var failed = recorder.Events.Single(e => e.EventId == 26);
        long running = TransactionEventSource.Log.AdmissionPauseTicks;
        while (TransactionEventSource.Log.AdmissionPauseTicks <= running)
        { ceiling.Token.ThrowIfCancellationRequested(); await Task.Yield(); }
        gate.IsAdmissionPaused.ShouldBeTrue();
        TransactionEventSource.Log.AdmissionPauseTicks.ShouldBeGreaterThan(running);
        _output.WriteLine($"Running pause total: {TransactionEventSource.Log.AdmissionPauseTicks} ticks");
        coordinator.BeforeCheckpoint = null;
        coordinator.Checkpoint(ceiling.Token);
        // Assert: payload names and process-unique episode identity survive event reordering.
        failed.PayloadNames.ShouldBe(new[] { "database", "reason", "episodeId" });
        failed.Payload![1].ShouldBe("CheckpointFailure");
        failed.Payload[2].ShouldBe(paused.Payload![6]);
        recorder.Events.Single(e => e.EventId == 24).Payload![3].ShouldBe(paused.Payload[6]);
        recorder.Events.ShouldNotContain(e => e.EventId == 0);
    }

    [Fact(DisplayName = "Cohesion Test [Transactions] - Half the limit requests a checkpoint with the size trigger disabled (#1283)")]
    public async Task Wait_SizeTriggerDisabled_ShouldRequestCheckpointAtHalfLimit()
    {
        // Arrange
        using var storage = CoordinatorStorage.Create();
        await using var coordinator = new TransactionCoordinator(storage, storage.Log, storage.Records);
        var gate = TransactionCheckpointInterlock.ForStorage(storage);
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using (gate.Enter("Catalog")) { using var bracket = storage.BeginTransaction(); bracket.Commit(); }
        gate.IsAdmissionPaused.ShouldBeFalse();

        // Act: reaching half the limit requests a checkpoint with the size trigger off.
        gate.JournalSizeLimit = 2;
        gate.ObserveAdmissionPause().ShouldBeTrue();
        var waiting = gate.WaitAsync(guard.Token, "Statement", 42).AsTask();
        waiting.IsCompleted.ShouldBeFalse();
        coordinator.Checkpoint(guard.Token);
        await waiting.WaitAsync(guard.Token);
        gate.Release();

        // Assert: the checkpoint's record cannot start a new pause.
        gate.IsAdmissionPaused.ShouldBeFalse();
        storage.JournalBytesSinceCheckpoint.ShouldBe(0);
    }

    [Theory(DisplayName = "Cohesion Test [Transactions] - Raw nested admission drains on success, exception and cancellation while paused (#1283)")]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task Enter_NestedHolderCrossesThreshold_ShouldDrainEveryExit(int exit)
    {
        // Arrange: catalog _sync precedes the gate, as it does in SQL and key/value.
        using var storage = CoordinatorStorage.Create();
        await using var coordinator = new TransactionCoordinator(storage, storage.Log, storage.Records);
        var gate = TransactionCheckpointInterlock.ForStorage(storage);
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        using var recorder = new EventSourceRecorder(TransactionEventSource.Log, EventLevel.Verbose);
        object catalogSync = new();
        gate.JournalSizeLimit = 2;
        storage.CheckpointJournalSize = 1;
        var expected = new IOException("raw exit");
        var holder = Task.Factory.StartNew(() =>
        {
            lock (catalogSync)
            {
                using var outer = gate.Enter("Catalog", 7, guard.Token);
                using var bracket = storage.BeginTransaction("Catalog", 7);
                using var nested = gate.Enter("Nested", 8, guard.Token);
                entered.Set(); release.Wait(guard.Token);
                // Nested re-entry must still work AFTER the pause is armed.
                using var afterPause = gate.Enter("NestedAfterPause", 9, guard.Token);
                if (exit == 1) { throw expected; }
                if (exit == 2) { throw new OperationCanceledException(guard.Token); }
                bracket.Commit();
            }
        }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        try
        {
            entered.Wait(guard.Token);
            gate.ObserveAdmissionPause().ShouldBeTrue();
            // Act: no new bracket can enter, but the holder can unwind without waiting on itself.
            var waiting = gate.WaitAsync(guard.Token, "Statement", 42).AsTask();
            waiting.IsCompleted.ShouldBeFalse();
            release.Set();
            if (exit == 1) { (await Should.ThrowAsync<IOException>(() => holder)).ShouldBeSameAs(expected); }
            else if (exit == 2) { await Should.ThrowAsync<OperationCanceledException>(() => holder); }
            else { await holder.WaitAsync(guard.Token); }
            await waiting.WaitAsync(guard.Token);
            gate.Release();
            // Assert
            gate.IsAdmissionPaused.ShouldBeFalse();
            var paused = recorder.Events.Single(e => e.EventId == 23);
            paused.PayloadNames.ShouldBe(new[] { "database", "reason", "bracketClass", "owner", "journalBytes", "journalSizeLimit", "episodeId" });
            paused.Payload![2].ShouldBe("Catalog"); paused.Payload[3].ShouldBe(7L);
            paused.Payload[4].ShouldBeOfType<long>().ShouldBeGreaterThanOrEqualTo(1); paused.Payload[5].ShouldBe(2L);
            recorder.Events.Count(e => e.EventId == 24).ShouldBe(1);
            recorder.Events.Count(e => e.EventId == 25).ShouldBe(1);
            recorder.Events.Single(e => e.EventId == 24).PayloadNames.ShouldBe(new[] { "database", "reason", "durationMilliseconds", "episodeId" });
            recorder.Events.Single(e => e.EventId == 25).PayloadNames.ShouldBe(new[] { "database", "bracketClass", "owner" });
            recorder.Events.ShouldNotContain(e => e.EventId == 0);
        }
        finally { release.Set(); try { await holder.WaitAsync(guard.Token); } catch (Exception e) when (e == expected || e is OperationCanceledException) { } }
    }

    [Theory(DisplayName = "Cohesion Test [Transactions] - A paused wait releases on cancellation, close and unconfigure (#1283)")]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task WaitAsync_PausedEarlyExit_ShouldWakeWithoutConsumingAdmission(int exit)
    {
        // Arrange
        using var storage = CoordinatorStorage.Create();
        await using var coordinator = new TransactionCoordinator(storage, storage.Log, storage.Records);
        var gate = TransactionCheckpointInterlock.ForStorage(storage);
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var canceled = new CancellationTokenSource();
        var failure = new IOException("checkpoint refused");
        coordinator.BeforeCheckpoint = _ => throw failure;
        using (gate.Enter("Seed")) { using var bracket = storage.BeginTransaction(); bracket.Commit(); }
        gate.JournalSizeLimit = 2; storage.CheckpointJournalSize = 1;
        var waiting = gate.WaitAsync(canceled.Token, "Statement").AsTask();
        waiting.IsCompleted.ShouldBeFalse();
        // Act
        if (exit == 0)
        {
            canceled.Cancel();
            TransactionAdmissionCanceledException? refused = null;
            try { await waiting; } catch (TransactionAdmissionCanceledException exception) { refused = exception; }
            refused.ShouldNotBeNull();
            refused.CancellationToken.ShouldBe(canceled.Token);
            refused.InnerException.ShouldBeAssignableTo<OperationCanceledException>();
        }
        else if (exit == 1)
        {
            storage.TakeOffline(StorageOfflineCause.CheckpointFailures, "checkpoint test", new IOException("test failure"));
            await Should.ThrowAsync<StorageOfflineException>(() => waiting.WaitAsync(guard.Token));
        }
        else
        {
            await coordinator.DisposeAsync();
            await waiting.WaitAsync(guard.Token); gate.Release();
        }
        // Assert: draining operations never wait behind the pause.
        coordinator.BeforeCheckpoint = null;
        if (exit != 2 && !storage.IsOffline) { coordinator.Checkpoint(guard.Token); }
        await gate.WaitAsync(guard.Token, "Undo", drain: true); gate.Release();
    }

    [Fact(DisplayName = "Cohesion Test [Transactions] - Explicit success suppresses old due work but retains an uncollected failure (#1283)")]
    public async Task Checkpoint_AfterDeferredFailure_ShouldResumeAndReportFailureExactlyOnce()
    {
        // Arrange
        using var storage = CoordinatorStorage.Create();
        await using var coordinator = new TransactionCoordinator(storage, storage.Log, storage.Records);
        var gate = TransactionCheckpointInterlock.ForStorage(storage);
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var failure = new IOException("retained checkpoint refusal");
        coordinator.BeforeCheckpoint = _ => throw failure;
        gate.JournalSizeLimit = 2; storage.CheckpointJournalSize = 1;
        using (gate.Enter("Seed")) { using var bracket = storage.BeginTransaction(); bracket.Commit(); }
        gate.ObserveAdmissionPause().ShouldBeTrue();
        var waiting = gate.WaitAsync(guard.Token, "Statement").AsTask();
        // Act: explicit success clears the pause, but the worker must still collect its earlier failure.
        coordinator.BeforeCheckpoint = null;
        coordinator.Checkpoint(guard.Token);
        await waiting.WaitAsync(guard.Token); gate.Release();
        // Assert: a new due cycle is not suppressed BEFORE the worker collects retained reporting state.
        using (gate.Enter("Next")) { using var bracket = storage.BeginTransaction(); bracket.Commit(); }
        gate.IsAdmissionPaused.ShouldBeFalse();
        storage.JournalBytesSinceCheckpoint.ShouldBe(0);
        int laterAttempts = 0;
        coordinator.BeforeCheckpoint = _ => { laterAttempts++; throw new IOException("later deferred failure"); };
        using (gate.Enter("LaterFailure")) { using var bracket = storage.BeginTransaction(); bracket.Commit(); }
        laterAttempts.ShouldBe(1); gate.ObserveAdmissionPause().ShouldBeTrue();
        // A later failure is observable by event but cannot overwrite the older handoff.
        Should.Throw<IOException>(() => coordinator.TryCheckpoint(TimeSpan.Zero, guard.Token)).ShouldBeSameAs(failure);
        coordinator.BeforeCheckpoint = null; coordinator.Checkpoint(guard.Token);
        coordinator.TryCheckpoint(TimeSpan.Zero, guard.Token).ShouldBeTrue();
    }

    [Fact(DisplayName = "Cohesion Test [Transactions] - Unconfigure clears a failed coordinator before rebind (#1283)")]
    public async Task UnconfigureCheckpoint_FailedPausedCoordinator_ShouldDropStaleStateBeforeRebind()
    {
        // Arrange
        using var storage = CoordinatorStorage.Create();
        await using var first = new TransactionCoordinator(storage, storage.Log, storage.Records);
        var gate = TransactionCheckpointInterlock.ForStorage(storage);
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        first.BeforeCheckpoint = _ => throw new IOException("old coordinator failure");
        gate.JournalSizeLimit = 2; storage.CheckpointJournalSize = 1;
        using (gate.Enter("Seed")) { using var bracket = storage.BeginTransaction(); bracket.Commit(); }
        gate.ObserveAdmissionPause().ShouldBeTrue();
        // Act
        await first.DisposeAsync();
        gate.JournalSizeLimit.ShouldBe(0); gate.IsAdmissionPaused.ShouldBeFalse();
        await using var second = new TransactionCoordinator(storage, storage.Log, storage.Records);
        // Assert: the new binding sees neither an old failure nor a suppressed retry.
        second.TryCheckpoint(TimeSpan.Zero, guard.Token).ShouldBeTrue();
        storage.JournalBytesSinceCheckpoint.ShouldBe(0);
    }

    [Fact(DisplayName = "Cohesion Test [Transactions] - A stale outer lease is inert before thread affinity is checked (#1283)")]
    public async Task Dispose_StaleOuterCopyAfterReentry_ShouldNotRefuseOrReleaseTheNewScope()
    {
        // Arrange / Act: keep both entries on one dedicated actor.
        using var storage = CoordinatorStorage.Create();
        var gate = TransactionCheckpointInterlock.ForStorage(storage);
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await Task.Factory.StartNew(() =>
        {
            var stale = gate.Enter("Old"); stale.Dispose();
            using var current = gate.Enter("Current");
            Task.Factory.StartNew(stale.Dispose, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default).WaitAsync(guard.Token).GetAwaiter().GetResult();
            // Assert: current still owns the gate.
            Task.Factory.StartNew(() => gate.TryCheckpoint(TimeSpan.Zero, guard.Token), CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default).GetAwaiter().GetResult().ShouldBeFalse();
        }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default).WaitAsync(guard.Token);
    }

    [Theory(DisplayName = "Cohesion Test [Transactions] - Whole and partial rollback drain through a pause and release model grants (#1283)")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RollbackAsync_PausedAdmissionWithLockWaiter_ShouldFinishUndoAndReleaseItsLocks(bool partial)
    {
        // Arrange
        using var storage = CoordinatorStorage.Create();
        await using var coordinator = new TransactionCoordinator(storage, storage.Log, storage.Records);
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var gate = TransactionCheckpointInterlock.ForStorage(storage);
        var writer = await coordinator.BeginAsync(IsolationLevel.ReadCommitted, guard.Token);
        var other = await coordinator.BeginAsync(IsolationLevel.ReadCommitted, guard.Token);
        var marker = partial ? await coordinator.CreateUndoMarkerAsync(writer, guard.Token) : null;
        var resource = LockResource.Object(42);
        await coordinator.LockManager.AcquireAsync(writer.Sequence, resource, LockMode.Exclusive, guard.Token);
        var location = await coordinator.ApplyStatementAsync(writer, bracket =>
        {
            var row = storage.Insert(bracket, Stamped(writer.Sequence, TransactionSequence.None));
            coordinator.VersionStore.RecordCreated(writer.Sequence, row.PageId, row.SlotIndex); return row;
        }, guard.Token);
        coordinator.BeforeCheckpoint = _ => throw new IOException("retry later");
        gate.JournalSizeLimit = 2; storage.CheckpointJournalSize = 1;
        var admission = gate.WaitAsync(guard.Token, "Statement").AsTask();
        var lockWaiter = coordinator.LockManager.AcquireAsync(other.Sequence, resource, LockMode.Exclusive, guard.Token).AsTask();
        lockWaiter.IsCompleted.ShouldBeFalse();
        // Act: undo must take the semaphore without waiting on checkpoint recovery.
        if (partial) { await coordinator.RollbackToUndoMarkerAsync(writer, marker!, guard.Token).AsTask().WaitAsync(guard.Token); }
        else { await coordinator.RollbackAsync(writer, guard.Token).AsTask().WaitAsync(guard.Token); }
        await lockWaiter.WaitAsync(guard.Token);
        // Assert
        storage.TryReadRecord(location.PageId, location.SlotIndex, out _).ShouldBeFalse();
        gate.ObserveAdmissionPause().ShouldBeTrue(); admission.IsCompleted.ShouldBeFalse();
        coordinator.BeforeCheckpoint = null; coordinator.Checkpoint(guard.Token);
        await admission.WaitAsync(guard.Token); gate.Release();
        await coordinator.RollbackAsync(other, guard.Token);
        if (partial) { await coordinator.RollbackAsync(writer, guard.Token); }
    }

    [Fact(DisplayName = "Cohesion Test [Transactions] - One huge admitted bracket reports its residual overshoot (#1283, #1270)")]
    public async Task ApplyStatement_HugeSingleBracket_ShouldCountPauseAndReportResidualOvershoot()
    {
        // Arrange
        using var storage = CoordinatorStorage.Create();
        await using var coordinator = new TransactionCoordinator(storage, storage.Log, storage.Records);
        var gate = TransactionCheckpointInterlock.ForStorage(storage);
        long pauses = TransactionEventSource.Log.AdmissionPauses;
        gate.JournalSizeLimit = 8192; storage.CheckpointJournalSize = 4096;
        long peak;
        // Act
        using (gate.Enter("HugeBracket"))
        {
            using var bracket = storage.BeginTransaction();
            var payload = new byte[2048]; new Random(1283).NextBytes(payload);
            for (int i = 0; i < 32; i++) { storage.Insert(bracket, payload); }
            bracket.Commit(); peak = storage.JournalBytesSinceCheckpoint;
        }
        // Assert: this existing #1270 residual is deliberately not an admission guarantee.
        _output.WriteLine($"single bracket: peak={peak}, limit={gate.JournalSizeLimit}, overshoot={peak - gate.JournalSizeLimit}");
        peak.ShouldBeGreaterThan(gate.JournalSizeLimit);
        TransactionEventSource.Log.AdmissionPauses.ShouldBe(pauses + 1);
        gate.IsAdmissionPaused.ShouldBeFalse();
    }
}
