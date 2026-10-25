using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.Tracing;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;

using Assimalign.Cohesion.Database.Storage;
using Assimalign.Cohesion.Database.Storage.Tests.TestObjects;
using Assimalign.Cohesion.Testing;

// Deviates from namespace-matches-assembly: linked test support keeps its owning namespace.
namespace Assimalign.Cohesion.Database.Tests;

internal static class CheckpointPace
{
    private const int minimumSamples = 12;

    internal static async Task VerifyAsync<TDatabase>(DatabaseCheckpointWorker<TDatabase> worker,
        ManualResetEventSlim signal, Assimalign.Cohesion.Database.Storage.Storage failingStorage, Assimalign.Cohesion.Database.Storage.Storage healthyStorage,
        string failingName, string healthyName, DeviceFaults faults,
        Func<int, Task> writeFailing, Func<int, Task> writeHealthy, long journalSize, int observerPauseMilliseconds = 0)
        where TDatabase : class
    {
        // Arrange: record the real pump and lanes. No explicit RunIteration or replacement waits.
        var passes = new ConcurrentQueue<DatabaseWorkerPass>();
        worker.PumpBackoffObserved = true;
        using var pump = WorkerPumpObservation.Create(worker, worker.RecordPumpBackoff, elapsed =>
        {
            // A test observer can deschedule the pump after a correct trigger. It must not
            // turn the recorded inter-pass duration into a correctness oracle.
            if (observerPauseMilliseconds > 0) { Thread.Sleep(observerPauseMilliseconds); }
            worker.RecordTriggerElapsed(elapsed);
        });
        worker.PassCompleted = passes.Enqueue;
        faults.FailPageWrites = true;
        var watch = Stopwatch.StartNew();
        int failingWrites = 0;
        int healthyWrites = 0;
        int completedBatches = 0;
        long pendingBatch = 0;
        int pendingPassCount = 0;

        // Act: create due work after each previous batch has completed. Writes finish before
        // the next signal, so statement-completed checkpoints cannot supply the progress proof.
        try
        {
            while (failingStorage.JournalLength < journalSize && watch.Elapsed < TimeSpan.FromSeconds(30))
            {
                await writeFailing(failingWrites);
                failingWrites += 10;
            }
            do
            {
                if (pendingBatch == 0)
                {
                    healthyStorage.CheckpointJournalSize = 0;
                    while (healthyStorage.JournalLength < journalSize && watch.Elapsed < TimeSpan.FromSeconds(30))
                    {
                        await writeHealthy(healthyWrites);
                        healthyWrites += 10;
                    }
                    pendingBatch = healthyStorage.JournalLength;
                    pendingPassCount = passes.Count;
                    healthyStorage.CheckpointJournalSize = journalSize;
                }
                signal.Set();
                await Task.Delay(5);
                if (pendingBatch != 0 && passes.Skip(pendingPassCount).Any(pass => pass.Visits.Any(visit =>
                    visit.Database == healthyName && (visit.Attempted || visit.Collected)
                    && visit.Reason == DatabaseWorkerPass.VisitReason.Completed)))
                {
                    pendingBatch = 0;
                    completedBatches++;
                }
                else if (pendingBatch != 0 && healthyStorage.JournalLength < journalSize)
                {
                    // A time-triggered checkpoint may have been deferred during preparation.
                    // Prepare another batch; only a recorded worker completion counts.
                    pendingBatch = 0;
                }
            }
            while ((completedBatches < 4 || worker.FailureCount < 2
                || passes.Count(pass => pass.Visits.Any(visit => visit.Database == failingName
                    && visit.Reason == DatabaseWorkerPass.VisitReason.BackingOff)) < 3
                || passes.Count(pass => pass.TriggerMeasured && pass.TriggerWaits > 0 && pass.TriggerSignaled) < minimumSamples
                || passes.Count(pass => pass.TriggerMeasured && pass.TriggerWaits > 0 && pass.TriggerSignaled && pass.PumpElapsed.HasValue) < minimumSamples
                || passes.Count(pass => pass.Visits.Any(visit => visit.Database == healthyName && visit.Attempted)) < minimumSamples
                || passes.Count(pass => pass.Visits.Any(visit => visit.Database == healthyName && visit.LaneWaits > 0)) < minimumSamples)
                && watch.Elapsed < TimeSpan.FromSeconds(30));
        }
        finally
        {
            worker.PassCompleted = null;
            // Keep this registration's capability stable for a pass that captured its listener
            // before detachment. The fixture releases this worker immediately after the helper.
            faults.Clear();
        }

        // Assert: progress is an attempted due visit, never a header written by a statement.
        var recorded = passes.ToArray();
        var laneBudget = worker.Interval < DatabaseCheckpointWorker<TDatabase>.PollInterval
            ? worker.Interval : DatabaseCheckpointWorker<TDatabase>.PollInterval;
        pump.Failure.ShouldBeNull();
        worker.PassObserverFailure.ShouldBeNull();
        foreach (var pass in recorded)
        {
            string context = $"pass {pass.Number}; " + DescribeDurations(recorded);
            pass.Cancelled.ShouldBeFalse(context);
            pass.PassThrew.ShouldBeFalse(context);
            pass.PumpBackoff.ShouldBe(TimeSpan.Zero, context);
            pass.TriggerTimeout.ShouldBeLessThanOrEqualTo(laneBudget, context);
            // The first wait may have begun before attachment; every subsequent pump wait
            // must carry its request record, even if an observer delays its completion.
            if (pass.TriggerMeasured && (pass.Number > 1 || pass.TriggerWaits > 0))
            {
                pass.TriggerWaits.ShouldBe(1, context);
                pass.TriggerWaitRequested.ShouldBe(pass.TriggerTimeout, context);
            }
            foreach (var visit in pass.Visits)
            {
                visit.GiveUpRequested.ShouldBeFalse(context);
                visit.Offline.ShouldBeFalse(context);
                if (visit.RunnerAvailable.HasValue) { visit.RunnerAvailable.ShouldBe(true, context); }
                // Bound the observed request independently of the production constant:
                // a quick device must not hide a quarter-second blocking request.
                visit.LaneWaitRequested.ShouldBeLessThanOrEqualTo(TimeSpan.FromMilliseconds(5) * visit.LaneWaits, context);
            }
            var healthy = pass.Visits.Single(visit => visit.Database == healthyName);
            healthy.Backoff.ShouldBe(TimeSpan.Zero, context);
            healthy.Reason.ShouldBeOneOf(DatabaseWorkerPass.VisitReason.Completed,
                DatabaseWorkerPass.VisitReason.NotDue, DatabaseWorkerPass.VisitReason.Running,
                DatabaseWorkerPass.VisitReason.Deferred, DatabaseWorkerPass.VisitReason.Busy);
            if (healthy.Attempted)
            {
                healthy.LaneWaitRequested.ShouldBeLessThanOrEqualTo(laneBudget + DatabaseWorkerLanes<bool>.WaitSlice);
                healthy.LaneWaits.ShouldBeLessThanOrEqualTo((int)(laneBudget / DatabaseWorkerLanes<bool>.WaitSlice) + 1);
            }
            if (healthy.Due is { } due)
            {
                if (due.IsDue) { healthy.Attempted.ShouldBeTrue($"due healthy visit on {context}"); }
            }
            if (healthy.Reason == DatabaseWorkerPass.VisitReason.NotDue)
            {
                healthy.Due.ShouldNotBeNull().IsDue.ShouldBeFalse($"NotDue evidence on {context}");
            }
            var failing = pass.Visits.Single(visit => visit.Database == failingName);
            if (failing.Reason == DatabaseWorkerPass.VisitReason.BackingOff)
            {
                failing.Backoff.ShouldBe(DatabaseEngineWorker.FailureBackoff);
                failing.LaneWaits.ShouldBe(0);
                failing.LaneWaitRequested.ShouldBe(TimeSpan.Zero);
            }
        }

        recorded.Count(pass => pass.Visits.Any(visit => visit.Database == healthyName && visit.RunnerAvailable == true))
            .ShouldBeGreaterThanOrEqualTo(4, "exercise completion-publication evidence");
        worker.FailureCount.ShouldBeGreaterThanOrEqualTo(2);
        recorded.Count(pass => pass.Visits.Any(visit => visit.Database == failingName
            && visit.Reason == DatabaseWorkerPass.VisitReason.BackingOff)).ShouldBeGreaterThanOrEqualTo(3);
        recorded.Count(pass => pass.Visits.Any(visit => visit.Database == healthyName && visit.Due is { IsDue: true }))
            .ShouldBeGreaterThanOrEqualTo(4);
        completedBatches.ShouldBeGreaterThanOrEqualTo(4);
        recorded.Count(pass => pass.Visits.Any(visit => visit.Database == healthyName && visit.LaneWaits > 0))
            .ShouldBeGreaterThanOrEqualTo(minimumSamples, "exercise actual lane wait requests");
        recorded.Count(pass => pass.TriggerMeasured && pass.TriggerWaits > 0 && pass.TriggerSignaled)
            .ShouldBeGreaterThanOrEqualTo(minimumSamples, DescribeDurations(recorded));
        recorded.Count(pass => pass.TriggerMeasured && pass.TriggerWaits > 0 && pass.TriggerSignaled && pass.PumpElapsed.HasValue)
            .ShouldBeGreaterThanOrEqualTo(minimumSamples, DescribeDurations(recorded));
        recorded.Count(pass => pass.Visits.Any(visit => visit.Database == healthyName && visit.Attempted))
            .ShouldBeGreaterThanOrEqualTo(minimumSamples, DescribeDurations(recorded));
    }

    // Hold dispatch before dirtying the database. Arming device I/O first can leave the
    // foreground writer waiting on the page latch that the deliberately stalled lane owns.
    internal static async Task HoldPageWritesAsync<TDatabase>(DatabaseWriteWorker<TDatabase> worker,
        TDatabase database, DeviceFaults faults, Func<Task> write)
        where TDatabase : class
    {
        var hangGuard = TimeSpan.FromSeconds(30);
        var duration = Stopwatch.StartNew();
        Task writing = Task.CompletedTask;
        bool armed = false;
        faults.StallPurges();
        worker.BeforeDatabase = current => { if (ReferenceEquals(current, database)) { faults.BeforePurge(); } };
        try
        {
            while (faults.StalledPurges == 0 && duration.Elapsed < hangGuard) { await Task.Delay(5); }
            faults.StalledPurges.ShouldBe(1, "hold the dispatched runner before touching storage");
            writing = DedicatedTestTask.Run(write);
            await writing.WaitAsync(hangGuard);
            faults.StallPageWrites();
            faults.ReleasePurges();
            while ((faults.StalledPurges != 0 || faults.StalledPageWrites == 0) && duration.Elapsed < hangGuard) { await Task.Delay(5); }
            faults.StalledPurges.ShouldBe(0);
            faults.StalledPageWrites.ShouldBe(1, "the prepared dirty page must reach the held device");
            armed = true;
        }
        finally
        {
            if (!armed) { faults.Clear(); }
            await writing.WaitAsync(hangGuard);
        }
    }

    // K = 4 positive-work completions, each published while exactly one device/lane runner
    // remains behind the test's gate. The 30 s ceiling is only a hang guard, never a pace floor.
    // Measured durations are diagnostic; scheduling decisions are checked through wait counters.
    internal static async Task VerifyIsolationAsync<TResult>(DatabaseLaneWorker<TResult> worker,
        string failingName, string healthyName, DeviceFaults faults, Func<int> stalled,
        Func<int, Task> writeHealthy, Func<Task>? afterResume = null, Func<int>? completedHealthyWork = null)
    {
        const int requiredCompletions = 4;
        var hangGuard = TimeSpan.FromSeconds(30);
        var passes = new ConcurrentQueue<(DatabaseWorkerPass Pass, int HealthyUnits)>();
        int pumpBackoffRequests = 0;
        int positiveCompletions = 0;
        int observedIsolation = 0;
        int counting = 0;
        int resumed = 0;
        long releasedAfter = long.MaxValue;
        string? isolationFailure = null;
        bool IsIsolated(DatabaseWorkerPass pass) => pass.Visits.Any(visit => visit.Database == failingName
            && !visit.Attempted && visit.Reason == DatabaseWorkerPass.VisitReason.Running);
        worker.PumpBackoffObserved = true;
        using var pump = WorkerPumpObservation.Create(worker, requested =>
        {
            Interlocked.Increment(ref pumpBackoffRequests);
            worker.RecordPumpBackoff(requested);
        }, worker.RecordTriggerElapsed);
        worker.PassCompleted = pass =>
        {
            var healthy = pass.Visits.SingleOrDefault(visit => visit.Database == healthyName);
            // Index compaction is a documented stub. Its fixture performs real page write-back
            // in BeforeDatabase and supplies that lane's FIFO result only after it is collected.
            int units = healthy?.Reason == DatabaseWorkerPass.VisitReason.Completed
                ? completedHealthyWork?.Invoke() ?? healthy.WorkUnits : 0;
            passes.Enqueue((pass, units));
            // A skipped-work mutant can spin the pump. Bound diagnostics independently of
            // cumulative progress so neither memory nor assertion cost grows with pass count.
            while (passes.Count > 64) { passes.TryDequeue(out _); }
            if (IsIsolated(pass))
            {
                Volatile.Write(ref observedIsolation, 1);
                if (Volatile.Read(ref counting) != 0)
                {
                    var held = pass.Visits.Single(visit => visit.Database == failingName);
                    bool invalid = pass.PassThrew || pass.Cancelled
                        || pass.Visits.Any(visit => visit.Reason == DatabaseWorkerPass.VisitReason.Failed
                            || visit.Offline || visit.GiveUpRequested)
                        || held.LaneWaits != 0 || healthy is null
                        || healthy.LaneWaits > (healthy.Attempted ? 1 : 0)
                        || healthy.Reason is DatabaseWorkerPass.VisitReason.BackingOff or DatabaseWorkerPass.VisitReason.Capacity
                        || (healthy.Reason == DatabaseWorkerPass.VisitReason.NotDue && healthy.WorkDue != false);
                    if (invalid && Volatile.Read(ref isolationFailure) is null)
                    {
                        Interlocked.CompareExchange(ref isolationFailure, $"invalid isolated pass {pass.Number}: "
                            + string.Join(", ", pass.Visits.Select(visit =>
                                $"{visit.Database} {visit.Reason} waits={visit.LaneWaits} offline={visit.Offline} giveUp={visit.GiveUpRequested}")), null);
                    }
                    if (units > 0 && healthy is { Reason: DatabaseWorkerPass.VisitReason.Completed }
                        && (healthy.Attempted || healthy.Collected))
                    {
                        Interlocked.Increment(ref positiveCompletions);
                    }
                }
            }
            if (pass.Number > Volatile.Read(ref releasedAfter) && pass.Visits.Any(visit => visit.Database == failingName
                && (visit.Attempted || visit.Collected) && visit.Reason == DatabaseWorkerPass.VisitReason.Completed))
            {
                Volatile.Write(ref resumed, 1);
            }
        };
        var duration = Stopwatch.StartNew();
        Task writing = Task.CompletedTask;
        int writes = 0;
        int Progress() => Volatile.Read(ref positiveCompletions);
        string Evidence() => $"{worker.Kind}: {Progress()}/{requiredCompletions} positive-work completions; "
            + $"held runners {stalled()}; elapsed {duration.Elapsed}; "
            + string.Join("; ", passes.TakeLast(8).Select(sample => $"pass {sample.Pass.Number} ({sample.Pass.Elapsed}): "
                + string.Join(", ", sample.Pass.Visits.Select(visit => $"{visit.Database} {visit.Reason} units={visit.WorkUnits} waits={visit.LaneWaits}"))));
        try
        {
            // Act: keep the fault held until progress is proved, even on a slow scheduler/device.
            while (stalled() == 0 && duration.Elapsed < hangGuard) { await Task.Delay(5); }
            stalled().ShouldBe(1, Evidence());
            // Let one complete pass collect any outcome left over from database creation.
            // Every subsequent positive completion then belongs to work performed under the gate.
            while (Volatile.Read(ref observedIsolation) == 0 && duration.Elapsed < hangGuard) { await Task.Delay(5); }
            Volatile.Read(ref observedIsolation).ShouldBe(1, Evidence());
            passes.Clear();
            Volatile.Write(ref counting, 1);
            do
            {
                stalled().ShouldBe(1, Evidence());
                int id = writes++;
                writing = DedicatedTestTask.Run(() => writeHealthy(id));
                await writing.WaitAsync(hangGuard);
                await Task.Delay(5);
            }
            while (Progress() < requiredCompletions && duration.Elapsed < hangGuard);

            // Assert before releasing: only this worker's completed, positive work counts.
            Volatile.Write(ref counting, 0);
            stalled().ShouldBe(1, Evidence());
            Progress().ShouldBeGreaterThanOrEqualTo(requiredCompletions, Evidence());
            Volatile.Read(ref isolationFailure).ShouldBeNull(Evidence());
            pump.Failure.ShouldBeNull();
            worker.PassObserverFailure.ShouldBeNull();
            Volatile.Read(ref pumpBackoffRequests).ShouldBe(0, Evidence());

            Volatile.Write(ref releasedAfter, passes.Last().Pass.Number);
            faults.Clear();
            duration.Restart();
            // A long-held gate can legitimately report a stuck-lane advisory. Collection and
            // root settlement must clear it; its lifetime count is not an operational failure.
            while ((Volatile.Read(ref resumed) == 0 || stalled() != 0 || worker.Fault is not null) && duration.Elapsed < hangGuard) { await Task.Delay(5); }
            Volatile.Read(ref resumed).ShouldBe(1, "the held worker must complete after release; " + Evidence());
            stalled().ShouldBe(0);
            worker.Fault.ShouldBeNull(Evidence());
            Volatile.Read(ref pumpBackoffRequests).ShouldBe(0, Evidence());
            if (afterResume is not null) { await afterResume(); }
        }
        finally
        {
            faults.Clear();
            await writing.WaitAsync(hangGuard);
            worker.PassCompleted = null;
        }
    }

    // A fake clock advances only after the prior operation has settled. RunIteration bounds
    // its wait and may leave a lane running even for fast work on a busy CI runner.
    internal static bool RunSettled<TResult>(DatabaseLaneWorker<TResult> worker, CancellationToken cancellationToken)
    {
        DatabaseWorkerPass? observed = null;
        var previous = worker.PassCompleted;
        worker.PassCompleted = pass =>
        {
            Volatile.Write(ref observed, pass);
            previous?.Invoke(pass);
        };
        var duration = Stopwatch.StartNew();
        bool succeeded = true;
        try
        {
            do
            {
                succeeded &= worker.RunIteration(cancellationToken);
                if (cancellationToken.IsCancellationRequested
                    || Volatile.Read(ref observed)?.Visits.Any(visit => visit.Reason == DatabaseWorkerPass.VisitReason.Running) != true)
                {
                    return succeeded;
                }
                worker.WaitForLanes(TimeSpan.FromSeconds(30)).ShouldBeTrue("settle the dispatched work before advancing the clock");
            }
            while (duration.Elapsed < TimeSpan.FromSeconds(30));
            throw new TimeoutException("Worker outcomes did not settle before the test ceiling.");
        }
        finally { worker.PassCompleted = previous; }
    }

    // The protected release seam is the existing public option for an unowned worker.
    // Test-only inheritance avoids a new grant from Database to the model test assemblies.
    internal static IAsyncDisposable Own(DatabaseEngineWorker worker) => new WorkerLease(worker);

    private sealed class WorkerLease : IAsyncDisposable
    {
        private readonly DatabaseEngineWorker _worker;
        internal WorkerLease(DatabaseEngineWorker worker) => _worker = worker;
        public ValueTask DisposeAsync() => WorkerLifetime.Release(_worker);
    }

    private abstract class WorkerLifetime : DatabaseEngine
    {
        private WorkerLifetime() : base("test-worker-lifetime", EngineModel.Sql) { }
        internal static ValueTask Release(DatabaseEngineWorker worker) => ReleaseUnownedWorkerAsync(worker);
    }

    // A delayed observer or descheduled pump may exceed every configured request. These
    // measurements help diagnose a hang but never decide whether a pass was correct.
    private static string DescribeDurations(IEnumerable<DatabaseWorkerPass> recorded)
        => string.Join("; ", recorded.TakeLast(8).Select(pass =>
            $"pass {pass.Number}: elapsed={pass.Elapsed}, trigger={pass.TriggerElapsed}, between={pass.PumpElapsed}, "
            + $"trigger requests={pass.TriggerWaits}/{pass.TriggerWaitRequested}, signaled={pass.TriggerSignaled}"));

    internal static DeferredUndoObservation<TDatabase> ObserveDeferredUndo<TDatabase>(
        DatabaseVersionPurgeWorker<TDatabase> worker, string database, TimeSpan retryDelay) where TDatabase : class => new(worker, database, retryDelay);

    internal sealed class DeferredUndoObservation<TDatabase> : IDisposable where TDatabase : class
    {
        private readonly DatabaseVersionPurgeWorker<TDatabase> _worker;
        private readonly WorkerPumpObservation _pump;
        private readonly EventSourceRecorder _undoEvents;
        private readonly string _database;
        private readonly TimeSpan _retryDelay;
        private int _failedUndoRetries;
        private string? _observationFailure;
        private readonly ConcurrentQueue<DatabaseWorkerPass> _passes = new();
        private readonly TaskCompletionSource<DatabaseWorkerPass> _completed = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal DeferredUndoObservation(DatabaseVersionPurgeWorker<TDatabase> worker, string database, TimeSpan retryDelay)
        {
            _worker = worker;
            _database = database;
            _retryDelay = retryDelay;
            _undoEvents = new EventSourceRecorder(EventSource.GetSources().Single(source =>
                source.Name == "Assimalign.Cohesion.Database.Transactions"), EventLevel.Informational);
            worker.PumpBackoffObserved = true;
            _pump = WorkerPumpObservation.Create(worker, worker.RecordPumpBackoff, worker.RecordTriggerElapsed);
            worker.PassCompleted = pass =>
            {
                // Failed worker retries double the coordinator's next delay. Count the facts
                // before deriving the next request bound; scheduler latency is never an input.
                var maximumRetry = TimeSpan.FromMilliseconds(Math.Min(worker.Interval.TotalMilliseconds,
                    _retryDelay.TotalMilliseconds * Math.Pow(2, Volatile.Read(ref _failedUndoRetries))));
                bool observedTrigger = pass.TriggerMeasured && (pass.Number > 1 || pass.TriggerWaits > 0);
                bool invalid = pass.PassThrew || pass.Cancelled || pass.PumpBackoff != TimeSpan.Zero
                    || observedTrigger && (pass.TriggerWaits != 1 || pass.TriggerWaitRequested != pass.TriggerTimeout)
                    || !pass.TriggerSignaled && pass.TriggerWaits > 0 && pass.TriggerWaitRequested > maximumRetry;
                if (invalid)
                {
                    Interlocked.CompareExchange(ref _observationFailure,
                        $"invalid undo pump request on pass {pass.Number}: backoff={pass.PumpBackoff}, "
                        + $"trigger={pass.TriggerWaits}/{pass.TriggerWaitRequested}, signaled={pass.TriggerSignaled}, maximum retry={maximumRetry}", null);
                }
                if (pass.Visits.Any(visit => visit.Database == database && visit.UndoRetried
                    && visit.Reason is DatabaseWorkerPass.VisitReason.Busy or DatabaseWorkerPass.VisitReason.Failed))
                {
                    Interlocked.Increment(ref _failedUndoRetries);
                }
                _passes.Enqueue(pass);
                while (_passes.Count > 64) { _passes.TryDequeue(out _); }
                if (pass.Visits.Any(visit => visit.Database == database && visit.UndoRetried
                    && !visit.UndoPending && visit.Reason == DatabaseWorkerPass.VisitReason.Completed))
                {
                    _completed.TrySetResult(pass);
                }
            };
        }

        internal async Task VerifyAsync()
        {
            // Assert: the real pump collected an undo-only lane, independently of the hour-long
            // full-maintenance interval. A foreground writer's completion alone is insufficient.
            DatabaseWorkerPass settled;
            try { settled = await _completed.Task.WaitAsync(TimeSpan.FromSeconds(30)); }
            catch (TimeoutException)
            {
                _completed.Task.IsCompleted.ShouldBeTrue("missing collected undo-only completion; " + DescribeDurations(_passes));
                throw;
            }
            string context = DescribeDurations(_passes);
            settled.PassThrew.ShouldBeFalse(context);
            settled.Cancelled.ShouldBeFalse(context);
            settled.PumpBackoff.ShouldBe(TimeSpan.Zero, context);
            Volatile.Read(ref _observationFailure).ShouldBeNull(context);
            var undo = settled.Visits.Single(visit => visit.UndoRetried);
            undo.WorkUnits.ShouldBe(0, "undo-only completion must not depend on full maintenance; " + context);
            undo.Offline.ShouldBeFalse(context);
            undo.GiveUpRequested.ShouldBeFalse(context);
            var completed = _undoEvents.Events.Where(item => item.EventId == 12
                && item.Payload is { Count: 3 } payload && Equals(payload[0], _database)).ShouldHaveSingleItem();
            completed.Payload![2].ShouldBeOfType<long>().ShouldBeGreaterThan(0L, "the retry must complete real undo work; " + context);
            _undoEvents.Events.ShouldNotContain(item => item.EventId == 0, context);
            _pump.Failure.ShouldBeNull(context);
            _worker.PassObserverFailure.ShouldBeNull(context);
        }

        public void Dispose()
        {
            _worker.PassCompleted = null;
            _pump.Dispose();
            _undoEvents.Dispose();
        }
    }
}
