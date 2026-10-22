using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Tracing;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Sql.Internal;
using Assimalign.Cohesion.Database.Storage;
using Assimalign.Cohesion.Database.Storage.Tests.TestObjects;
using Assimalign.Cohesion.Database.Tests;
using Assimalign.Cohesion.Testing;

namespace Assimalign.Cohesion.Database.Sql.Tests;

[Collection(nameof(SqlWorkerResilienceTests))]
public sealed class MaintenanceCapacityTests
{
    [Fact(DisplayName = "Cohesion Test [Database.Sql] - Write-back pacing: a stuck-detection timeout collects observations without advancing healthy peers")]
    public async Task Run_StuckDeadlineBeforeWriteBackInterval_ShouldKeepHealthyPeersOnTheirInterval()
    {
        // Arrange: 1.5-second intervals put the four-second stuck deadline between regular ticks.
        await using var engine = SqlDatabaseEngine.Create("stuck-pacing", new SqlDatabaseEngineOptions
        {
            CheckpointInterval = TimeSpan.FromHours(1), PageWriteBackInterval = TimeSpan.FromMilliseconds(1_500),
            MaintenanceInterval = TimeSpan.FromHours(1),
        });
        var held = await engine.CreateDatabaseAsync("held");
        await engine.CreateDatabaseAsync("healthy0");
        await engine.CreateDatabaseAsync("healthy1");
        var worker = engine.Workers.OfType<SqlPageWriteBackWorker>().Single();
        using var release = new ManualResetEventSlim();
        var observed = new ConcurrentQueue<DatabaseWorkerPass>();
        worker.BeforeDatabase = current => { if (ReferenceEquals(current, held)) { release.Wait(); } };
        worker.PassCompleted = pass =>
        {
            if (worker.Fault is TimeoutException && !pass.TriggerSignaled
                && pass.TriggerTimeout < worker.Interval) { observed.Enqueue(pass); }
        };
        try
        {
            // Act: wait for a real stuck observation, keeping its I/O held throughout.
            var duration = Stopwatch.StartNew();
            while (observed.IsEmpty && duration.Elapsed < TimeSpan.FromSeconds(15)) { await Task.Delay(5); }

            // Assert: a watchdog wake is not an extra regular write-back interval.
            observed.IsEmpty.ShouldBeFalse();
            var pass = observed.First();
            var healthy = pass.Visits.Where(visit => visit.Database.StartsWith("healthy", StringComparison.Ordinal)).ToArray();
            healthy.Length.ShouldBe(2);
            healthy.ShouldAllBe(visit => !visit.Attempted && visit.Reason == DatabaseWorkerPass.VisitReason.NotDue);
            engine.HasFailingWorker(held.Name).ShouldBeFalse();
        }
        finally { release.Set(); worker.BeforeDatabase = null; worker.PassCompleted = null; }
    }

    [Fact(DisplayName = "Cohesion Test [Database.Sql] - WAL fairness: one free production slot serves every healthy database within N passes")]
    public async Task RunIteration_OneFreeSlot_ShouldServeEveryHealthyDatabaseWithinTheirCountOfPasses()
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("one-free-slot", new SqlDatabaseEngineOptions
        {
            CheckpointInterval = TimeSpan.FromHours(1), PageWriteBackInterval = TimeSpan.FromHours(1),
            MaintenanceInterval = TimeSpan.FromHours(1),
        });
        var databases = new SqlDatabase[71];
        for (int index = 0; index < databases.Length; index++) { databases[index] = await engine.CreateDatabaseAsync("db" + index); }
        using var signal = new ManualResetEventSlim();
        using var held = new ManualResetEventSlim();
        using var finite = new ManualResetEventSlim();
        var worker = new SqlWriteAheadFlushWorker(engine, signal);
        await using var lifetime = CheckpointPace.Own(worker);
        var served = new int[8];
        int finished = 0;
        worker.BeforeDatabase = database =>
        {
            int index = Array.IndexOf(databases, database);
            if (index < 63) { held.Wait(); }
            else
            {
                finite.Wait();
                Interlocked.Increment(ref served[index - 63]);
                Interlocked.Increment(ref finished);
            }
        };
        try
        {
            // Act: each pass returns exactly one finite slot while the other 63 stay held.
            for (int pass = 0; pass < served.Length; pass++)
            {
                finite.Reset();
                worker.RunIteration(CancellationToken.None);
                finite.Set();
                SpinWait.SpinUntil(() => Volatile.Read(ref finished) >= pass + 1, TimeSpan.FromSeconds(30)).ShouldBeTrue();
            }

            // Assert: earlier completed WAL lanes cannot take the free slot again first.
            served.ShouldAllBe(count => count >= 1);
        }
        finally { held.Set(); finite.Set(); worker.WaitForLanes(TimeSpan.FromSeconds(30)).ShouldBeTrue(); }
    }

    [Fact(DisplayName = "Cohesion Test [Database.Sql] - Write-back pacing: completion wakes collect the held database without advancing its healthy peers")]
    public async Task Run_DetachedWriteBackCompletes_ShouldKeepOtherDatabasesOnTheirInterval()
    {
        // Arrange: observe the real pump, for at least six completion/interval races.
        var duration = Stopwatch.StartNew();
        int rounds = 0;
        do
        {
            await using var engine = SqlDatabaseEngine.Create("paced-completion", new SqlDatabaseEngineOptions
            {
                CheckpointInterval = TimeSpan.FromHours(1), PageWriteBackInterval = TimeSpan.FromMilliseconds(300),
                MaintenanceInterval = TimeSpan.FromHours(1),
            });
            var held = await engine.CreateDatabaseAsync("held");
            await engine.CreateDatabaseAsync("healthy0");
            await engine.CreateDatabaseAsync("healthy1");
            var worker = engine.Workers.OfType<SqlPageWriteBackWorker>().Single();
            using var release = new ManualResetEventSlim();
            var passes = new ConcurrentQueue<DatabaseWorkerPass>();
            int healthy = 0;
            worker.PassCompleted = passes.Enqueue;
            worker.BeforeDatabase = current =>
            {
                if (ReferenceEquals(current, held)) { release.Wait(); }
                else { Interlocked.Increment(ref healthy); }
            };
            try
            {
                // Act: release after the interval pass, before the next interval is due.
                var race = Stopwatch.StartNew();
                while (passes.IsEmpty && race.Elapsed < TimeSpan.FromSeconds(30)) { await Task.Delay(5); }
                passes.IsEmpty.ShouldBeFalse();
                healthy.ShouldBe(2);
                release.Set();
                while (!passes.Any(pass => pass.Visits.Any(visit => visit.Database == "held" && visit.Collected))
                    && race.Elapsed < TimeSpan.FromSeconds(30)) { await Task.Delay(5); }

                // Assert: a completion pass only settles/follows up its completed database.
                passes.Any(pass => pass.Visits.Any(visit => visit.Database == "held" && visit.Collected)).ShouldBeTrue();
                healthy.ShouldBe(2);
                rounds++;
            }
            finally { release.Set(); worker.BeforeDatabase = null; worker.PassCompleted = null; }
        }
        while ((duration.Elapsed < TimeSpan.FromMilliseconds(250) || rounds < 6) && duration.Elapsed < TimeSpan.FromSeconds(30));
        rounds.ShouldBeGreaterThanOrEqualTo(6);
    }

    [Fact(DisplayName = "Cohesion Test [Database.Sql] - Disposal: held I/O stays owned and periodic events name its database and worker")]
    public async Task DisposeAsync_LaneStillRunning_ShouldReportWhileKeepingStorageOpen()
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("dispose-lane", new SqlDatabaseEngineOptions
        {
            CheckpointInterval = TimeSpan.FromHours(1), PageWriteBackInterval = TimeSpan.FromHours(1),
            MaintenanceInterval = TimeSpan.FromHours(1),
        });
        var database = await engine.CreateDatabaseAsync("held");
        var worker = engine.Workers.OfType<SqlPageWriteBackWorker>().Single();
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        using var events = new EventSourceRecorder(EventSource.GetSources().Single(source =>
            source.Name == "Assimalign.Cohesion.Database"), EventLevel.Warning);
        worker.BeforeDatabase = _ => { entered.Set(); release.Wait(); };
        Task disposal = Task.CompletedTask;
        try
        {
            // Act: the disposal deadline is deliberately unbounded; only reporting is periodic.
            worker.RunIteration(CancellationToken.None);
            entered.Wait(TimeSpan.FromSeconds(30)).ShouldBeTrue();
            disposal = DedicatedTestTask.Run(async () => await engine.DisposeAsync());
            var duration = Stopwatch.StartNew();
            while (events.Events.Count(item => item.EventId == 36) < 2 && duration.Elapsed < TimeSpan.FromSeconds(10))
            {
                await Task.Delay(10);
            }

            // Assert
            var waiting = events.Events.Where(item => item.EventId == 36).ToArray();
            waiting.Length.ShouldBeGreaterThanOrEqualTo(2);
            foreach (var item in waiting)
            {
                item.PayloadNames!.ToArray().ShouldBe(new[] { "workerName", "workerKind", "database", "elapsedSeconds" });
                item.Payload![0].ShouldBe(worker.Name);
                item.Payload[1].ShouldBe(nameof(DatabaseEngineWorkerKind.PageWriteBack));
                item.Payload[2].ShouldBe("held");
            }
            events.Events.ShouldNotContain(item => item.EventId == 0);
            disposal.IsCompleted.ShouldBeFalse();
            database.IsClosed.ShouldBeFalse();
        }
        finally { release.Set(); await disposal.WaitAsync(TimeSpan.FromSeconds(30)); }
    }

    [Fact(DisplayName = "Cohesion Test [Database.Sql] - Lane recovery: stuck slots are reclaimed under a hard ceiling of 128 dedicated threads")]
    public void Run_StuckAtProductionCap_ShouldReclaimSlotsWithoutExceedingHardCeiling()
    {
        // Arrange
        using var lanes = new DatabaseWorkerLanes<int>("hard ceiling");
        using var signal = new ManualResetEventSlim(true);
        using var release = new ManualResetEventSlim();
        var keys = Enumerable.Range(0, 129).Select(_ => new object()).ToArray();
        lanes.BeginPass();
        try
        {
            // Act: all admitted operations remain held, including after their slots are reclaimed.
            for (int index = 0; index < 64; index++)
            {
                lanes.Run(keys[index], () => { release.Wait(); return 1; }, signal,
                    TimeSpan.Zero, CancellationToken.None, out _).ShouldBeFalse();
            }
            var duration = Stopwatch.StartNew();
            int admitted = 64;
            do
            {
                lanes.BeginPass();
                if (!lanes.IsSaturated)
                {
                    lanes.Run(keys[admitted], () => { release.Wait(); return 1; }, signal,
                        TimeSpan.Zero, CancellationToken.None, out _).ShouldBeFalse();
                    if (lanes.IsRunning(keys[admitted])) { admitted++; }
                }
                Thread.Sleep(5);
            }
            while (admitted < 128 && duration.Elapsed < TimeSpan.FromSeconds(10));
            admitted.ShouldBe(128);
            lanes.Run(keys[128], static () => 1, signal, TimeSpan.Zero, CancellationToken.None, out _).ShouldBeFalse();

            // Assert
            lanes.RunnerCount.ShouldBe(128);
            lanes.Collect(keys[128], out _).ShouldBe(DatabaseWorkerLanes<int>.LaneState.Idle);
        }
        finally { release.Set(); }
    }

    [Theory(DisplayName = "Cohesion Test [Database.Sql] - Idle workers: an unobserved idle visit allocates nothing and dispatches no lane")]
    [InlineData(DatabaseEngineWorkerKind.PageWriteBack)]
    [InlineData(DatabaseEngineWorkerKind.WriteAheadFlush)]
    [InlineData(DatabaseEngineWorkerKind.VersionPurge)]
    [InlineData(DatabaseEngineWorkerKind.IndexMaintenance)]
    public async Task RunIteration_IdleDatabase_ShouldAllocateNothingPerWorkerKind(DatabaseEngineWorkerKind kind)
    {
        // Arrange: the checkpoint allocation regression lives in CheckpointPassObservationTests.
        await using var engine = SqlDatabaseEngine.Create("idle-cost", new SqlDatabaseEngineOptions
        {
            CheckpointInterval = TimeSpan.FromHours(1), PageWriteBackInterval = TimeSpan.FromHours(1),
            MaintenanceInterval = TimeSpan.FromHours(1),
        });
        var database = await engine.CreateDatabaseAsync("idle");
        database.Coordinator.Checkpoint();
        var worker = engine.Workers.Single(current => current.Kind == kind);
        for (int pass = 0; pass < 100; pass++) { worker.RunIteration(CancellationToken.None); }

        // Act
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int pass = 0; pass < 1_000; pass++) { worker.RunIteration(CancellationToken.None); }
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        // Assert
        allocated.ShouldBe(0);
        worker.Fault.ShouldBeNull();
    }

    [Fact(DisplayName = "Cohesion Test [Database.Sql] - WAL capacity: one held and 63 finite lanes cannot starve database 65")]
    public async Task RunIteration_ProductionCapWithFiniteLanes_ShouldRotateAdmission()
    {
        // Arrange: the actual SQL flusher, with finite work held until each pass returns.
        await using var engine = SqlDatabaseEngine.Create("fair-cap", new SqlDatabaseEngineOptions
        {
            CheckpointInterval = TimeSpan.FromHours(1), PageWriteBackInterval = TimeSpan.FromHours(1),
            MaintenanceInterval = TimeSpan.FromHours(1),
        });
        var databases = new SqlDatabase[65];
        for (int index = 0; index < databases.Length; index++) { databases[index] = await engine.CreateDatabaseAsync("db" + index); }
        using var signal = new ManualResetEventSlim();
        using var held = new ManualResetEventSlim();
        using var finite = new ManualResetEventSlim();
        var worker = new SqlWriteAheadFlushWorker(engine, signal);
        int finiteCompleted = 0;
        int healthyVisits = 0;
        worker.BeforeDatabase = database =>
        {
            int index = Array.IndexOf(databases, database);
            if (index == 0) { held.Wait(); }
            else if (index == 64) { Interlocked.Increment(ref healthyVisits); }
            else { finite.Wait(); Interlocked.Increment(ref finiteCompleted); }
        };
        int servedBySecondPass = 0;
        try
        {
            // Act: repeat Review 1's 16-pass probe; every finite batch returns capacity.
            for (int pass = 0; pass < 16; pass++)
            {
                finite.Reset();
                int started = finiteCompleted;
                worker.RunIteration(CancellationToken.None);
                finite.Set();
                SpinWait.SpinUntil(() => finiteCompleted >= started + 62, TimeSpan.FromSeconds(30)).ShouldBeTrue();
                if (pass == 1) { servedBySecondPass = healthyVisits; }
            }
            // Assert: late-commit rechecks cannot consume the same 64 slots forever.
            servedBySecondPass.ShouldBeGreaterThan(0);
            healthyVisits.ShouldBeGreaterThan(0);
            finiteCompleted.ShouldBeGreaterThanOrEqualTo(16 * 62);
        }
        finally
        {
            held.Set(); finite.Set();
            worker.WaitForLanes(TimeSpan.FromSeconds(30)).ShouldBeTrue();
            await SqlDatabaseEngine.ReleaseRefusedWorkerAsync(worker);
        }
    }

    [Theory(DisplayName = "Cohesion Test [Database.Sql] - Capacity: 70 hung databases leave all 30 healthy databases maintained and report saturation")]
    [InlineData(DatabaseEngineWorkerKind.Checkpoint)]
    [InlineData(DatabaseEngineWorkerKind.PageWriteBack)]
    [InlineData(DatabaseEngineWorkerKind.VersionPurge)]
    public async Task RunIteration_SeventyHungThirtyHealthy_ShouldMaintainEveryHealthyDatabase(DatabaseEngineWorkerKind kind)
    {
        // Arrange: real engine workers and storage, with a held device/apply surrogate.
        var clock = new ManualTimeProvider();
        await using var engine = SqlDatabaseEngine.Create("recover-cap", new SqlDatabaseEngineOptions
        {
            CheckpointInterval = TimeSpan.FromHours(1), PageWriteBackInterval = TimeSpan.FromHours(1),
            MaintenanceInterval = TimeSpan.FromHours(1), TimeProvider = clock,
            WorkerFailureMinimumPasses = 1, WorkerFailureWindow = TimeSpan.FromSeconds(1),
        });
        var databases = new SqlDatabase[100];
        for (int index = 0; index < databases.Length; index++)
        {
            var database = databases[index] = await engine.CreateDatabaseAsync("db" + index);
            await using var session = await database.CreateSessionAsync();
            await session.ExecuteAsync("CREATE TABLE t (id INT)");
            await session.ExecuteAsync("INSERT INTO t VALUES (1)");
            database.DataStorage.OnCheckpointNeeded = null;
            database.CatalogStorage.OnCheckpointNeeded = null;
            if (kind == DatabaseEngineWorkerKind.Checkpoint)
            {
                database.DataStorage.CheckpointJournalSize = 1;
                database.CatalogStorage.CheckpointJournalSize = 1;
            }
        }
        using var held = new ManualResetEventSlim();
        var worker = engine.Workers.Single(current => current.Kind == kind);
        var checkpoint = engine.Workers.OfType<SqlCheckpointWorker>().Single();
        var write = engine.Workers.OfType<SqlPageWriteBackWorker>().Single();
        var purge = engine.Workers.OfType<SqlVersionPurgeWorker>().Single();
        using var events = new EventSourceRecorder(EventSource.GetSources().Single(source =>
            source.Name == "Assimalign.Cohesion.Database"), EventLevel.Warning);
        var completed = new int[100];
        int hung = 0;
        void Hold(SqlDatabase database)
        {
            if (Array.IndexOf(databases, database) < 70)
            {
                Interlocked.Increment(ref hung);
                if (kind == DatabaseEngineWorkerKind.Checkpoint) { engine.CheckpointNeededSignal.Set(); }
                held.Wait();
            }
        }
        void Observe(DatabaseWorkerPass pass)
        {
            foreach (var visit in pass.Visits.Where(visit => visit.Reason == DatabaseWorkerPass.VisitReason.Completed
                && (kind == DatabaseEngineWorkerKind.Checkpoint || visit.WorkUnits > 0)))
            {
                int index = Array.FindIndex(databases, database => database.Name.ToString() == visit.Database);
                if (index >= 0) { Interlocked.Increment(ref completed[index]); }
            }
        }
        if (kind == DatabaseEngineWorkerKind.Checkpoint) { checkpoint.BeforeCheckpoint = Hold; checkpoint.PassCompleted = Observe; }
        if (kind == DatabaseEngineWorkerKind.PageWriteBack) { write.BeforeDatabase = Hold; write.PassCompleted = Observe; }
        if (kind == DatabaseEngineWorkerKind.VersionPurge) { purge.BeforeDatabase = Hold; purge.PassCompleted = Observe; }
        clock.Advance(TimeSpan.FromHours(1));
        var duration = Stopwatch.StartNew();
        try
        {
            // Act: capacity must be reclaimed without releasing any of the hung lanes.
            do { worker.RunIteration(CancellationToken.None); await Task.Delay(10); }
            while (completed.Skip(70).Any(count => count == 0) && duration.Elapsed < TimeSpan.FromSeconds(15));

            // Assert: every healthy database makes real worker progress and stays available.
            completed.Skip(70).Count(count => count > 0).ShouldBe(30);
            hung.ShouldBeGreaterThanOrEqualTo(64);
            worker.FailureCount.ShouldBeGreaterThan(0);
            worker.Fault.ShouldNotBeNull();
            engine.State.ShouldBe(EngineState.Faulted);
            foreach (string reason in new[] { "capacity", "stuck" })
            {
                events.Events.Any(item => item.EventId == 35 && item.Payload![0]!.Equals(worker.Name)
                    && item.Payload[2] is string name && name.StartsWith("db", StringComparison.Ordinal)
                    && item.Payload[3]!.Equals(reason)).ShouldBeTrue();
            }
            events.Events.ShouldNotContain(item => item.EventId == 0);
            foreach (var database in databases) { engine.HasFailingWorker(database.Name).ShouldBeFalse(); }
            engine.OfflineDatabases.ShouldBeEmpty();
        }
        finally
        {
            held.Set();
            checkpoint.BeforeCheckpoint = null; write.BeforeDatabase = null; purge.BeforeDatabase = null;
            checkpoint.PassCompleted = null; write.PassCompleted = null; purge.PassCompleted = null;
        }
    }
}
