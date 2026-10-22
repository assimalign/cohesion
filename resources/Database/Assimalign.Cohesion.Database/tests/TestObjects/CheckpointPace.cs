using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;

using Assimalign.Cohesion.Database.Storage;
using Assimalign.Cohesion.Database.Storage.Tests.TestObjects;

// Deviates from namespace-matches-assembly: linked test support keeps its owning namespace.
namespace Assimalign.Cohesion.Database.Tests;

internal static class CheckpointPace
{
    private const int minimumSamples = 12;

    internal static async Task VerifyAsync<TDatabase>(DatabaseCheckpointWorker<TDatabase> worker,
        ManualResetEventSlim signal, Assimalign.Cohesion.Database.Storage.Storage failingStorage, Assimalign.Cohesion.Database.Storage.Storage healthyStorage,
        string failingName, string healthyName, DeviceFaults faults,
        Func<int, Task> writeFailing, Func<int, Task> writeHealthy, long journalSize)
        where TDatabase : class
    {
        // Arrange: record the real pump and lanes. No explicit RunIteration or replacement waits.
        var passes = new ConcurrentQueue<DatabaseWorkerPass>();
        worker.PumpBackoffObserved = true;
        using var pump = WorkerPumpObservation.Create(worker, worker.RecordPumpBackoff, worker.RecordTriggerElapsed);
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
            while ((watch.Elapsed < TimeSpan.FromMilliseconds(250) || completedBatches < 4 || worker.FailureCount < 2
                || passes.Count(pass => pass.Visits.Any(visit => visit.Database == failingName
                    && visit.Reason == DatabaseWorkerPass.VisitReason.BackingOff)) < 3
                || passes.Count(pass => pass.TriggerMeasured && pass.TriggerSignaled) < minimumSamples
                || passes.Count(pass => pass.TriggerMeasured && pass.TriggerSignaled && pass.PumpElapsed.HasValue) < minimumSamples
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
            string context = $"pass {pass.Number}";
            pass.Cancelled.ShouldBeFalse(context);
            pass.PassThrew.ShouldBeFalse(context);
            pass.PumpBackoff.ShouldBe(TimeSpan.Zero, context);
            pass.TriggerTimeout.ShouldBeLessThanOrEqualTo(DatabaseCheckpointWorker<TDatabase>.PollInterval);
            foreach (var visit in pass.Visits)
            {
                visit.GiveUpRequested.ShouldBeFalse(context);
                visit.Offline.ShouldBeFalse(context);
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
        VerifyDurations(recorded.Select(pass => pass.Elapsed), laneBudget, "whole checkpoint pass");
        worker.FailureCount.ShouldBeGreaterThanOrEqualTo(2);
        recorded.Count(pass => pass.Visits.Any(visit => visit.Database == failingName
            && visit.Reason == DatabaseWorkerPass.VisitReason.BackingOff)).ShouldBeGreaterThanOrEqualTo(3);
        recorded.Count(pass => pass.Visits.Any(visit => visit.Database == healthyName && visit.Due is { IsDue: true }))
            .ShouldBeGreaterThanOrEqualTo(4);
        completedBatches.ShouldBeGreaterThanOrEqualTo(4);
        recorded.Count(pass => pass.Visits.Any(visit => visit.Database == healthyName && visit.LaneWaits > 0))
            .ShouldBeGreaterThanOrEqualTo(minimumSamples, "exercise actual lane wait requests");
        var signaled = recorded.Where(pass => pass.TriggerMeasured && pass.TriggerSignaled).ToArray();
        VerifyDurations(signaled.Select(pass => pass.TriggerElapsed), laneBudget, "measured Set wake");
        VerifyDurations(signaled.Where(pass => pass.PumpElapsed.HasValue).Select(pass => pass.PumpElapsed!.Value),
            laneBudget, "measured inter-pass wait");
        VerifyDurations(recorded.SelectMany(pass => pass.Visits)
            .Where(visit => visit.Database == healthyName && visit.Attempted).Select(visit => visit.LaneWait),
            laneBudget, "healthy lane wait");
    }

    // Observe real pump passes while a device or apply-gate surrogate stays held. Physical
    // progress alone could come from committer self-help; attempted/collected worker visits
    // prove this worker made progress. Aggregate timing catches persistent scheduling mutants.
    internal static async Task VerifyIsolationAsync<TResult>(DatabaseLaneWorker<TResult> worker,
        string failingName, string healthyName, DeviceFaults faults, Func<int> stalled,
        Func<int, Task> writeHealthy, Func<Task>? beforeRelease = null)
    {
        var passes = new ConcurrentQueue<DatabaseWorkerPass>();
        worker.PumpBackoffObserved = true;
        using var pump = WorkerPumpObservation.Create(worker, worker.RecordPumpBackoff, worker.RecordTriggerElapsed);
        worker.PassCompleted = passes.Enqueue;
        var duration = Stopwatch.StartNew();
        int writes = 0;
        try
        {
            while (stalled() == 0 && duration.Elapsed < TimeSpan.FromSeconds(30)) { await Task.Delay(5); }
            stalled().ShouldBe(1, "the fault must hold actual work");
            do
            {
                await writeHealthy(writes++).WaitAsync(TimeSpan.FromSeconds(30));
                await Task.Delay(5);
            }
            while ((duration.Elapsed < TimeSpan.FromMilliseconds(250)
                || passes.Count(pass => pass.Visits.Any(visit => visit.Database == failingName
                    && visit.Reason == DatabaseWorkerPass.VisitReason.Running)) < minimumSamples
                || passes.Count(pass => pass.Visits.Any(visit => visit.Database == healthyName
                    && (visit.Attempted || visit.Collected) && visit.Reason == DatabaseWorkerPass.VisitReason.Completed)) < minimumSamples
                || (worker.Kind != DatabaseEngineWorkerKind.IndexMaintenance
                    && passes.Count(pass => pass.Visits.Any(visit => visit.Database == failingName
                        && visit.Reason == DatabaseWorkerPass.VisitReason.Running)
                        && pass.Visits.Any(visit => visit.Database == healthyName && visit.WorkUnits > 0)) < 4)
                || passes.Count(pass => pass.TriggerMeasured && pass.PumpElapsed.HasValue) < minimumSamples)
                && duration.Elapsed < TimeSpan.FromSeconds(30));

            stalled().ShouldBe(1, "healthy work must progress before releasing the stalled database");
            var recorded = passes.ToArray();
            var isolated = recorded.Where(pass => pass.Visits.Any(visit => visit.Database == failingName
                && visit.Reason == DatabaseWorkerPass.VisitReason.Running)).ToArray();
            isolated.Length.ShouldBeGreaterThanOrEqualTo(minimumSamples);
            isolated.Count(pass => pass.Visits.Any(visit => visit.Database == healthyName
                && (visit.Attempted || visit.Collected) && visit.Reason == DatabaseWorkerPass.VisitReason.Completed))
                .ShouldBeGreaterThanOrEqualTo(minimumSamples);
            foreach (var pass in isolated)
            {
                pass.PassThrew.ShouldBeFalse();
                pass.PumpBackoff.ShouldBe(TimeSpan.Zero);
                var healthy = pass.Visits.Single(visit => visit.Database == healthyName);
                healthy.Backoff.ShouldBe(TimeSpan.Zero);
                if (healthy.Reason == DatabaseWorkerPass.VisitReason.NotDue)
                {
                    healthy.WorkDue.ShouldBe(false, "the pump must provide its due decision for an idle visit");
                }
                else { (healthy.Attempted || healthy.Collected || healthy.Reason == DatabaseWorkerPass.VisitReason.Running).ShouldBeTrue(); }
            }
            if (worker.Kind != DatabaseEngineWorkerKind.IndexMaintenance)
            {
                isolated.Count(pass => pass.Visits.Any(visit => visit.Database == healthyName && visit.WorkUnits > 0))
                    .ShouldBeGreaterThanOrEqualTo(4, "actual worker I/O or full purge, not committer self-help");
            }
            var budget = TimeSpan.FromMilliseconds(100);
            VerifyDurations(isolated.Select(pass => pass.Elapsed), budget, "isolated whole pass");
            VerifyDurations(isolated.Where(pass => pass.PumpElapsed.HasValue).Select(pass => pass.PumpElapsed!.Value), budget, "isolated pump pace");
            VerifyDurations(isolated.Where(pass => pass.TriggerMeasured).Select(pass => pass.TriggerElapsed), budget, "isolated trigger wait");
            VerifyDurations(isolated.SelectMany(pass => pass.Visits).Where(visit => visit.Database == healthyName)
                .Select(visit => visit.LaneWait), budget, "isolated healthy lane wait");
            pump.Failure.ShouldBeNull();
            worker.PassObserverFailure.ShouldBeNull();
            worker.FailureCount.ShouldBe(0);
            if (beforeRelease is not null) { await beforeRelease(); }
        }
        finally
        {
            worker.PassCompleted = null;
            faults.Clear();
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

    private static void VerifyDurations(IEnumerable<TimeSpan> measured, TimeSpan budget, string description)
    {
        var samples = measured.Order().ToArray();
        string context = $"{description}: budget {budget}; sorted samples ({samples.Length}): [{string.Join(", ", samples)}]";
        samples.Length.ShouldBeGreaterThanOrEqualTo(minimumSamples, context);
        var median = TimeSpan.FromTicks((samples[(samples.Length - 1) / 2].Ticks + samples[samples.Length / 2].Ticks) / 2);
        median.ShouldBeLessThan(budget, context);
        samples.Count(sample => sample < budget).ShouldBeGreaterThanOrEqualTo((samples.Length * 3 + 3) / 4, context);
    }
}
