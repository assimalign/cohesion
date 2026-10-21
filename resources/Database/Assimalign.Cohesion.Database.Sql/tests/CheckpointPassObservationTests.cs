using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Sql.Tests.TestObjects;
using Assimalign.Cohesion.Database.Storage;

namespace Assimalign.Cohesion.Database.Sql.Tests;

[Collection(nameof(SqlWorkerResilienceTests))]
public sealed class CheckpointPassObservationTests
{
    [Theory(DisplayName = "Cohesion Test [Database.Sql] - Checkpoint observations: every visit exit records its reason")]
    [InlineData("Completed")]
    [InlineData("Deferred")]
    [InlineData("Failed")]
    [InlineData("NotDue")]
    [InlineData("Offline")]
    [InlineData("Closed")]
    [InlineData("Busy")]
    public void RunIteration_VisitEnds_ShouldRecordItsOutcome(string reason)
    {
        // Arrange
        using var worker = new ObservedCheckpointWorker();
        var database = worker.Databases[0];
        database.Due = reason != "NotDue";
        database.Offline = reason == "Offline";
        database.Open = reason != "Closed";
        worker.Work = reason switch
        {
            "Deferred" => static () => false,
            "Failed" => static () => throw new InvalidOperationException("checkpoint failed"),
            "Closed" => static () => throw new ObjectDisposedException("database"),
            "Busy" => static () => throw new StorageTransactionException("storage busy"),
            _ => static () => true,
        };
        var passes = new List<DatabaseCheckpointPass>();
        worker.PassCompleted = passes.Add;

        // Act: a lane may finish after the initial bounded wait on a loaded machine.
        worker.RunIteration(CancellationToken.None);
        worker.WaitForCheckpoints(TimeSpan.FromSeconds(30)).ShouldBeTrue();
        if (passes[^1].Visits[0].Reason == "Running") { worker.RunIteration(CancellationToken.None); }

        // Assert
        var visit = passes[^1].Visits.Single();
        visit.Database.ShouldBe(database.Name);
        visit.Reason.ShouldBe(reason);
        visit.Backoff.ShouldBe(reason == "Failed" ? DatabaseEngineWorker.FailureBackoff : TimeSpan.Zero);
        visit.LaneWait.ShouldBeGreaterThanOrEqualTo(TimeSpan.Zero);
        if (reason == "Failed")
        {
            worker.RunIteration(CancellationToken.None);
            passes[^1].Visits.Single().Reason.ShouldBe("BackingOff");
            passes[^1].Visits.Single().LaneWaits.ShouldBe(0);
        }
    }

    [Fact(DisplayName = "Cohesion Test [Database.Sql] - Checkpoint observations: a concurrent explicit pass cannot consume the pump's pending wait")]
    public async Task RunIteration_ConcurrentPumpWait_ShouldKeepWaitsOnTheirThread()
    {
        // Arrange
        using var worker = new ObservedCheckpointWorker();
        worker.Databases[0].Due = false;
        var passes = new List<DatabaseCheckpointPass>();
        worker.PassCompleted = passes.Add;
        var delay = TimeSpan.FromMilliseconds(250);
        var duration = Stopwatch.StartNew();
        int rounds = 0;

        // Act: hold one thread's pending wait while another thread completes an explicit pass.
        do
        {
            using var recorded = new ManualResetEventSlim();
            using var release = new ManualResetEventSlim();
            var pump = Task.Run(() =>
            {
                worker.RecordPumpBackoff(delay);
                recorded.Set();
                release.Wait();
                worker.RunIteration(CancellationToken.None);
            });
            try
            {
                recorded.Wait(TimeSpan.FromSeconds(30)).ShouldBeTrue();
                worker.RunIteration(CancellationToken.None);
            }
            finally
            {
                release.Set();
                await pump.WaitAsync(TimeSpan.FromSeconds(30));
            }
            rounds++;
        }
        while ((duration.Elapsed < TimeSpan.FromMilliseconds(250) || rounds < 3)
            && duration.Elapsed < TimeSpan.FromSeconds(30));

        // Assert
        rounds.ShouldBeGreaterThanOrEqualTo(3);
        passes.Count.ShouldBe(rounds * 2);
        for (int round = 0; round < rounds; round++)
        {
            passes[round * 2].PumpBackoff.ShouldBe(TimeSpan.Zero);
            passes[round * 2 + 1].PumpBackoff.ShouldBe(delay);
        }
    }

    [Fact(DisplayName = "Cohesion Test [Database.Sql] - Checkpoint observations: snapshot failure and cancellation publish and release the pass")]
    public void RunIteration_SnapshotFailsOrPassCancels_ShouldPublishAndAllowAnotherPass()
    {
        // Arrange
        using var worker = new ObservedCheckpointWorker();
        var passes = new List<DatabaseCheckpointPass>();
        worker.PassCompleted = passes.Add;
        worker.FailSnapshot = true;

        // Act
        worker.RunIteration(CancellationToken.None).ShouldBeFalse();
        worker.FailSnapshot = false;
        using var stop = new CancellationTokenSource();
        stop.Cancel();
        worker.RunIteration(stop.Token);
        worker.RunIteration(CancellationToken.None);

        // Assert
        passes.Count.ShouldBe(3);
        passes[0].Failed.ShouldBeTrue();
        passes[0].Visits.ShouldBeEmpty();
        passes[1].Cancelled.ShouldBeTrue();
        passes[1].Visits.Single().Reason.ShouldBe("Cancelled");
        passes[2].Failed.ShouldBeFalse();
        passes[2].Cancelled.ShouldBeFalse();
        passes.Select(pass => pass.Number).ShouldBe([1L, 2L, 3L]);
    }

    [Fact(DisplayName = "Cohesion Test [Database.Sql] - Checkpoint observations: lane detachment races cancellation, completion and disposal without retaining a latch")]
    public async Task Run_DetachmentRacesCompletionAndDisposal_ShouldReleaseEveryLane()
    {
        // Arrange
        var duration = Stopwatch.StartNew();
        int detached = 0;
        int cancelled = 0;
        int failed = 0;
        int disposed = 0;
        int disposalWaits = 0;

        // Act: each round forces the wait to begin before cancelling or detaching it. Continue
        // after the minimum time until every path's counter proves it ran, with a hard ceiling.
        do
        {
            using var lanes = new DatabaseCheckpointLanes("observation stress");
            using var signal = new ManualResetEventSlim();
            using var entered = new ManualResetEventSlim();
            using var release = new ManualResetEventSlim();
            using var stop = new CancellationTokenSource();
            object database = new();
            bool cancel = disposed % 2 == 0;
            bool fail = disposed % 3 == 0;
            var visit = new DatabaseCheckpointPass.Visit("stalled");
            lanes.BeginPass();
            var run = Task.Run(() =>
            {
                try
                {
                    return lanes.Run(database, () =>
                    {
                        entered.Set();
                        release.Wait();
                        if (fail) { throw new InvalidOperationException("lane failed"); }
                        return true;
                    }, signal, TimeSpan.FromSeconds(30), stop.Token, out _, visit);
                }
                catch (OperationCanceledException) when (stop.IsCancellationRequested)
                {
                    Interlocked.Increment(ref cancelled);
                    return false;
                }
            });
            Thread? disposal = null;
            try
            {
                entered.Wait(TimeSpan.FromSeconds(30)).ShouldBeTrue();
                SpinWait.SpinUntil(() => visit.LaneWaits > 0, TimeSpan.FromSeconds(30)).ShouldBeTrue();
                if (cancel) { stop.Cancel(); } else { signal.Set(); }
                (await run.WaitAsync(TimeSpan.FromSeconds(30))).ShouldBeFalse();
                detached++;
                visit.LaneWaits.ShouldBeGreaterThanOrEqualTo(1);
                lanes.Collect(database, out _).ShouldBe(DatabaseCheckpointLanes.LaneState.Running);
                lanes.WaitForIdle(TimeSpan.Zero).ShouldBeFalse();
                disposal = new Thread(lanes.Dispose) { IsBackground = true };
                disposal.Start();
                SpinWait.SpinUntil(() => (disposal.ThreadState & System.Threading.ThreadState.WaitSleepJoin) != 0,
                    TimeSpan.FromSeconds(30)).ShouldBeTrue();
                disposalWaits++;
            }
            finally
            {
                // Even a failed assertion releases the checkpoint before joining any waiter.
                release.Set();
                await run.WaitAsync(TimeSpan.FromSeconds(30));
                if (disposal is not null) { disposal.Join(TimeSpan.FromSeconds(30)).ShouldBeTrue(); }
            }
            lanes.WaitForIdle(TimeSpan.FromSeconds(30)).ShouldBeTrue();
            lanes.Collect(database, out var outcome).ShouldBe(DatabaseCheckpointLanes.LaneState.Ended);
            if (fail) { outcome.Failure.ShouldBeOfType<InvalidOperationException>(); failed++; }
            else { outcome.Finished.ShouldBeTrue(); }
            lanes.EndPass();
            Should.Throw<ObjectDisposedException>(() => lanes.Run(new object(), static () => true,
                signal, TimeSpan.Zero, CancellationToken.None, out _));
            disposed++;
        }
        while ((duration.Elapsed < TimeSpan.FromMilliseconds(250) || detached < 6 || cancelled < 3 || failed < 2)
            && duration.Elapsed < TimeSpan.FromSeconds(30));

        // Assert
        detached.ShouldBeGreaterThanOrEqualTo(6);
        cancelled.ShouldBeGreaterThanOrEqualTo(3);
        failed.ShouldBeGreaterThanOrEqualTo(2);
        disposed.ShouldBe(detached);
        disposalWaits.ShouldBe(disposed);
    }
}
