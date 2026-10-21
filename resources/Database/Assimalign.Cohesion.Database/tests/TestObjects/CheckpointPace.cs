using System;
using System.Collections.Concurrent;
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
    internal static async Task VerifyAsync<TDatabase>(DatabaseCheckpointWorker<TDatabase> worker,
        ManualResetEventSlim signal, Assimalign.Cohesion.Database.Storage.Storage failingStorage, Assimalign.Cohesion.Database.Storage.Storage healthyStorage,
        string failingName, string healthyName, DeviceFaults faults,
        Func<int, Task> writeFailing, Func<int, Task> writeHealthy, long journalSize)
        where TDatabase : class
    {
        // Arrange: record the real pump and lanes. No explicit RunIteration or replacement waits.
        var passes = new ConcurrentQueue<DatabaseCheckpointPass>();
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
                if (pendingBatch == 0 && completedBatches < 4)
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
                    && visit.Reason == DatabaseCheckpointPass.VisitReason.Completed)))
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
                    && visit.Reason == DatabaseCheckpointPass.VisitReason.BackingOff)) < 3)
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
            if (pass.TriggerMeasured && pass.TriggerSignaled)
            {
                pass.TriggerElapsed.ShouldBeLessThan(pass.TriggerTimeout, $"measured Set wake on {context}");
                if (pass.PumpElapsed is { } idle)
                {
                    idle.ShouldBeLessThan(pass.TriggerTimeout, $"measured inter-pass wait on {context}");
                }
            }
            var healthy = pass.Visits.Single(visit => visit.Database == healthyName);
            healthy.Backoff.ShouldBe(TimeSpan.Zero, context);
            healthy.Reason.ShouldBeOneOf(DatabaseCheckpointPass.VisitReason.Completed,
                DatabaseCheckpointPass.VisitReason.NotDue, DatabaseCheckpointPass.VisitReason.Running,
                DatabaseCheckpointPass.VisitReason.Deferred, DatabaseCheckpointPass.VisitReason.Busy);
            if (healthy.Attempted)
            {
                healthy.LaneWait.ShouldBeLessThan(laneBudget, $"healthy lane wait on {context}");
                healthy.LaneWaitRequested.ShouldBeLessThanOrEqualTo(laneBudget + DatabaseCheckpointLanes.WaitSlice);
                healthy.LaneWaits.ShouldBeLessThanOrEqualTo((int)(laneBudget / DatabaseCheckpointLanes.WaitSlice) + 1);
            }
            if (healthy.Due is { } due)
            {
                if (due.IsDue) { healthy.Attempted.ShouldBeTrue($"due healthy visit on {context}"); }
            }
            if (healthy.Reason == DatabaseCheckpointPass.VisitReason.NotDue)
            {
                healthy.Due.ShouldNotBeNull().IsDue.ShouldBeFalse($"NotDue evidence on {context}");
            }
            var failing = pass.Visits.Single(visit => visit.Database == failingName);
            if (failing.Reason == DatabaseCheckpointPass.VisitReason.BackingOff)
            {
                failing.Backoff.ShouldBe(DatabaseEngineWorker.FailureBackoff);
                failing.LaneWaits.ShouldBe(0);
                failing.LaneWaitRequested.ShouldBe(TimeSpan.Zero);
            }
        }
        worker.FailureCount.ShouldBeGreaterThanOrEqualTo(2);
        recorded.Count(pass => pass.Visits.Any(visit => visit.Database == failingName
            && visit.Reason == DatabaseCheckpointPass.VisitReason.BackingOff)).ShouldBeGreaterThanOrEqualTo(3);
        recorded.Count(pass => pass.Visits.Any(visit => visit.Database == healthyName && visit.Due is { IsDue: true }))
            .ShouldBeGreaterThanOrEqualTo(4);
        completedBatches.ShouldBeGreaterThanOrEqualTo(4);
        recorded.Count(pass => pass.TriggerMeasured && pass.TriggerSignaled).ShouldBeGreaterThanOrEqualTo(4);
    }
}
