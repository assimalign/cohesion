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
    [Fact(DisplayName = "Cohesion Test [Database.Sql] - Checkpoint observations: an unobserved idle pass allocates no observation state")]
    public void RunIteration_NoObserver_ShouldAllocateNoObservationState()
    {
        // Arrange: no lane work or changing snapshots; warm the existing pass machinery.
        using var worker = new ObservedCheckpointWorker();
        worker.Databases[0].Due = false;
        for (int pass = 0; pass < 100; pass++) { worker.RunIteration(CancellationToken.None); }

        // Act
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int pass = 0; pass < 1_000; pass++) { worker.RunIteration(CancellationToken.None); }
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        // Assert
        allocated.ShouldBe(0);
    }

    [Fact(DisplayName = "Cohesion Test [Database.Sql] - Checkpoint observations: an absent delay hook differs from no requested delay")]
    public void RunIteration_DelayHookAbsent_ShouldRecordNotObserved()
    {
        // Arrange
        using var worker = new ObservedCheckpointWorker();
        worker.Databases[0].Due = false;
        DatabaseWorkerPass? pass = null;
        worker.PassCompleted = observed => pass = observed;

        // Act
        worker.RunIteration(CancellationToken.None);

        // Assert
        pass.ShouldNotBeNull().PumpBackoff.ShouldBeNull();
        pass.TriggerMeasured.ShouldBeFalse();
    }

    [Fact(DisplayName = "Cohesion Test [Database.Sql] - Checkpoint observations: callback failure leaves pass and pump failure policy unchanged")]
    public void RunIteration_ObserverThrows_ShouldRetainFailureWithoutFailingThePass()
    {
        // Arrange
        using var worker = new ObservedCheckpointWorker();
        worker.Databases[0].Due = false;
        var failure = new InvalidOperationException("observer failed");
        worker.PassCompleted = _ => throw failure;

        // Act
        bool succeeded = worker.RunIteration(CancellationToken.None);
        worker.PassCompleted = null;
        bool nextSucceeded = worker.RunIteration(CancellationToken.None);

        // Assert
        succeeded.ShouldBeTrue();
        nextSucceeded.ShouldBeTrue();
        worker.PassObserverFailure.ShouldBeSameAs(failure);
        worker.FailureCount.ShouldBe(0);
        worker.Fault.ShouldBeNull();
    }

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
        var passes = new List<DatabaseWorkerPass>();
        worker.PassCompleted = passes.Add;

        // Act: a lane may finish after the initial bounded wait on a loaded machine.
        worker.RunIteration(CancellationToken.None);
        worker.WaitForCheckpoints(TimeSpan.FromSeconds(30)).ShouldBeTrue();
        if (passes[^1].Visits[0].Reason == DatabaseWorkerPass.VisitReason.Running) { worker.RunIteration(CancellationToken.None); }

        // Assert
        var visit = passes[^1].Visits.Single();
        visit.Database.ShouldBe(database.Name);
        visit.Reason.ShouldBe(Enum.Parse<DatabaseWorkerPass.VisitReason>(reason));
        visit.Backoff.ShouldBe(reason == "Failed" ? DatabaseEngineWorker.FailureBackoff : TimeSpan.Zero);
        visit.LaneWait.ShouldBeGreaterThanOrEqualTo(TimeSpan.Zero);
        if (reason == "Failed")
        {
            worker.RunIteration(CancellationToken.None);
            passes[^1].Visits.Single().Reason.ShouldBe(DatabaseWorkerPass.VisitReason.BackingOff);
            passes[^1].Visits.Single().LaneWaits.ShouldBe(0);
        }
    }

    [Fact(DisplayName = "Cohesion Test [Database.Sql] - Checkpoint observations: a concurrent explicit pass cannot consume the pump's pending wait")]
    public async Task RunIteration_ConcurrentPumpWait_ShouldKeepWaitsOnTheirThread()
    {
        // Arrange
        using var worker = new ObservedCheckpointWorker();
        worker.Databases[0].Due = false;
        var passes = new List<DatabaseWorkerPass>();
        worker.PassCompleted = passes.Add;
        worker.PumpBackoffObserved = true;
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
        var passes = new List<DatabaseWorkerPass>();
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
        passes[0].PassThrew.ShouldBeTrue();
        passes[0].Visits.ShouldBeEmpty();
        passes[1].Cancelled.ShouldBeTrue();
        passes[1].Visits.ShouldBeEmpty();
        passes[2].PassThrew.ShouldBeFalse();
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
            using var lanes = new DatabaseWorkerLanes<bool>("observation stress");
            using var signal = new ManualResetEventSlim();
            using var entered = new ManualResetEventSlim();
            using var release = new ManualResetEventSlim();
            using var stop = new CancellationTokenSource();
            object database = new();
            bool cancel = disposed % 2 == 0;
            bool fail = disposed % 3 == 0;
            var visit = new DatabaseWorkerPass.Visit("stalled");
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
                lanes.Collect(database, out _).ShouldBe(DatabaseWorkerLanes<bool>.LaneState.Running);
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
            lanes.Collect(database, out var outcome).ShouldBe(DatabaseWorkerLanes<bool>.LaneState.Ended);
            if (fail) { outcome.Failure.ShouldBeOfType<InvalidOperationException>(); failed++; }
            else { outcome.Result.ShouldBeTrue(); }
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
    [Fact(DisplayName = "Cohesion Test [Database.Sql] - Worker lanes: thread cap, failed start and released capacity retain ownership")]
    public async Task Run_CapacityAndFailedStart_ShouldDeferWorkAndRecoverWithoutLeakingRunners()
    {
        // Arrange
        var duration = Stopwatch.StartNew();
        int saturated = 0;
        int recovered = 0;
        int startFailed = 0;
        do
        {
            using var lanes = new DatabaseWorkerLanes<int>("bounded lanes", maximumLanes: 1);
            using var signal = new ManualResetEventSlim();
            using var release = new ManualResetEventSlim();
            using var entered = new ManualResetEventSlim();
            object first = new();
            object second = new();
            lanes.BeginPass();
            try
            {
                // Act: a failed thread start returns its slot before the next dispatch.
                lanes.BeforeStart = () => throw new InvalidOperationException("injected start failure");
                Should.Throw<InvalidOperationException>(() => lanes.Run(first, () => 1, signal,
                    TimeSpan.Zero, CancellationToken.None, out _));
                lanes.Collect(first, out _).ShouldBe(DatabaseWorkerLanes<int>.LaneState.Idle);
                startFailed++;
                lanes.BeforeStart = null;
                lanes.Run(first, () => { entered.Set(); release.Wait(); return 42; }, signal,
                    TimeSpan.Zero, CancellationToken.None, out _).ShouldBeFalse();
                entered.Wait(TimeSpan.FromSeconds(30)).ShouldBeTrue();
                var visit = new DatabaseWorkerPass.Visit("deferred");
                lanes.Run(second, () => throw new InvalidOperationException("must not run"), signal,
                    TimeSpan.Zero, CancellationToken.None, out _, visit).ShouldBeFalse();
                visit.Reason.ShouldBe(DatabaseWorkerPass.VisitReason.Capacity);
                lanes.Collect(second, out _).ShouldBe(DatabaseWorkerLanes<int>.LaneState.Idle);
                saturated++;
            }
            finally { release.Set(); }
            lanes.WaitForIdle(TimeSpan.FromSeconds(30)).ShouldBeTrue();
            lanes.Collect(first, out var ended).ShouldBe(DatabaseWorkerLanes<int>.LaneState.Ended);
            ended.Result.ShouldBe(42);
            bool complete = false;
            while (!complete && duration.Elapsed < TimeSpan.FromSeconds(30))
            {
                complete = lanes.Run(second, () => 7, signal, TimeSpan.FromSeconds(1), CancellationToken.None, out ended);
                if (!complete)
                {
                    lanes.WaitForIdle(TimeSpan.FromSeconds(30)).ShouldBeTrue();
                    complete = lanes.Collect(second, out ended) == DatabaseWorkerLanes<int>.LaneState.Ended;
                }
            }
            // Assert: the existing runner or its replacement serves the deferred database.
            complete.ShouldBeTrue();
            ended.Result.ShouldBe(7);
            recovered++;
            lanes.EndPass();
        }
        while ((duration.Elapsed < TimeSpan.FromMilliseconds(250) || recovered < 6) && duration.Elapsed < TimeSpan.FromSeconds(30));
        recovered.ShouldBeGreaterThanOrEqualTo(6);
        saturated.ShouldBe(recovered);
        startFailed.ShouldBe(recovered);
    }

    [Fact(DisplayName = "Cohesion Test [Database.Sql] - Worker lanes: idle retirement returns the thread slot for later work")]
    public void Run_IdleRunnerRetires_ShouldStartAReplacementWithoutExceedingTheCap()
    {
        // Arrange
        var duration = Stopwatch.StartNew();
        int retired = 0;
        int replacements = 0;
        do
        {
            using var lanes = new DatabaseWorkerLanes<int>("retiring lanes", maximumLanes: 1,
                idleTimeout: TimeSpan.FromMilliseconds(1));
            using var signal = new ManualResetEventSlim();
            int starts = 0;
            lanes.BeforeStart = () => Interlocked.Increment(ref starts);
            lanes.BeginPass();
            // Act: wait for observed retirement, then dispatch work on the reclaimed slot.
            for (int work = 0; work < 2; work++)
            {
                object key = new();
                if (!lanes.Run(key, () => 42, signal, TimeSpan.FromSeconds(30), CancellationToken.None, out var outcome))
                {
                    lanes.WaitForIdle(TimeSpan.FromSeconds(30)).ShouldBeTrue();
                    lanes.Collect(key, out outcome).ShouldBe(DatabaseWorkerLanes<int>.LaneState.Ended);
                }
                outcome.Result.ShouldBe(42);
                SpinWait.SpinUntil(() => lanes.RunnerCount == 0, TimeSpan.FromSeconds(30)).ShouldBeTrue();
                retired++;
            }
            // Assert: both jobs needed a runner, and neither retained the single slot.
            starts.ShouldBe(2);
            replacements++;
            lanes.EndPass();
        }
        while ((duration.Elapsed < TimeSpan.FromMilliseconds(250) || replacements < 6)
            && duration.Elapsed < TimeSpan.FromSeconds(30));
        replacements.ShouldBeGreaterThanOrEqualTo(6);
        retired.ShouldBe(replacements * 2);
    }

    [Fact(DisplayName = "Cohesion Test [Database.Sql] - Worker lanes: a cancelled detached pass is collected without faulting a later pass")]
    public async Task RunIteration_OriginatingTokenCancelsAfterDetachment_ShouldKeepCancellationOutOfFailureReporting()
    {
        // Arrange
        var duration = Stopwatch.StartNew();
        int cancelled = 0;
        do
        {
            using var worker = new ObservedCheckpointWorker();
            using var signal = new ManualResetEventSlim();
            using var release = new ManualResetEventSlim();
            using var stop = new CancellationTokenSource();
            var passes = new List<DatabaseWorkerPass>();
            worker.PassCompleted = passes.Add;
            worker.Work = () =>
            {
                signal.Set();
                release.Wait();
                stop.Token.ThrowIfCancellationRequested();
                return true;
            };
            // Act: cancellation ends the waiting pass, then the lane observes that same token.
            var run = Task.Run(() => worker.RunIteration(stop.Token));
            try
            {
                signal.Wait(TimeSpan.FromSeconds(30)).ShouldBeTrue();
                stop.Cancel();
                await Should.ThrowAsync<OperationCanceledException>(async () => { await run.WaitAsync(TimeSpan.FromSeconds(30)); });
            }
            finally { release.Set(); }
            worker.WaitForCheckpoints(TimeSpan.FromSeconds(30)).ShouldBeTrue();
            worker.RunIteration(CancellationToken.None);
            // Assert: the later pass's uncancelled token cannot turn prior cancellation into a fault.
            passes[^1].Visits.Single().Reason.ShouldBe(DatabaseWorkerPass.VisitReason.Cancelled);
            worker.FailureCount.ShouldBe(0);
            worker.Fault.ShouldBeNull();
            cancelled++;
        }
        while ((duration.Elapsed < TimeSpan.FromMilliseconds(250) || cancelled < 6)
            && duration.Elapsed < TimeSpan.FromSeconds(30));
        cancelled.ShouldBeGreaterThanOrEqualTo(6);
    }

    [Fact(DisplayName = "Cohesion Test [Database.Sql] - Worker lanes: caller and prior database ambient state cannot enter a lane")]
    public void Run_CallerAndPriorJobSetAsyncLocal_ShouldUseCleanContextForNewAndReusedRunners()
    {
        // Arrange
        var duration = Stopwatch.StartNew();
        var ambient = new AsyncLocal<int>();
        int jobs = 0;
        int rounds = 0;
        try
        {
            do
            {
                using var lanes = new DatabaseWorkerLanes<int>("isolated context", maximumLanes: 1);
                using var signal = new ManualResetEventSlim();
                lanes.BeginPass();
                for (int job = 0; job < 2; job++)
                {
                    ambient.Value = 42 + job;
                    object key = new();
                    DatabaseWorkerLanes<int>.Outcome outcome = default;
                    bool completed = false;
                    // Act: the second job must reuse the one admitted runner. Capacity can be
                    // briefly occupied between result publication and the runner parking.
                    while (!completed && duration.Elapsed < TimeSpan.FromSeconds(30))
                    {
                        completed = lanes.Run(key, () =>
                        {
                            int inherited = ambient.Value;
                            ambient.Value = 99;
                            return inherited;
                        }, signal, TimeSpan.FromSeconds(1), CancellationToken.None, out outcome);
                        if (!completed)
                        {
                            lanes.WaitForIdle(TimeSpan.FromSeconds(30)).ShouldBeTrue();
                            completed = lanes.Collect(key, out outcome) == DatabaseWorkerLanes<int>.LaneState.Ended;
                            if (!completed) { Thread.Yield(); }
                        }
                    }
                    // Assert: neither caller state nor the previous job's state leaked in.
                    completed.ShouldBeTrue();
                    outcome.Failure.ShouldBeNull();
                    outcome.Result.ShouldBe(0);
                    ambient.Value.ShouldBe(42 + job);
                    jobs++;
                }
                lanes.EndPass();
                rounds++;
            }
            while ((duration.Elapsed < TimeSpan.FromMilliseconds(250) || rounds < 6)
                && duration.Elapsed < TimeSpan.FromSeconds(30));
        }
        finally { ambient.Value = 0; }
        rounds.ShouldBeGreaterThanOrEqualTo(6);
        jobs.ShouldBe(rounds * 2);
    }

}
