using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.Tracing;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Connections;
using Assimalign.Cohesion.Connections.InMemory;
using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.Protocol;
using Assimalign.Cohesion.Database.Graph.Internal;
using Assimalign.Cohesion.Database.Storage;
using Assimalign.Cohesion.Database.Storage.Tests.TestObjects;
using Assimalign.Cohesion.Database.Tests;

using Assimalign.Cohesion.Database.Graph.Protocol;

using Assimalign.Cohesion.Testing;

namespace Assimalign.Cohesion.Database.Graph.Tests;

/// <summary>
/// The graph engine's background workers under device faults that fire on their own threads
/// (#1268): a checkpoint or page write-back whose page writes fail is reported while the fault
/// lasts, retried after the worker's backoff, and recovers when the fault clears, while the
/// engine's other database keeps its work; a failed group-commit drain (#1252) or fsync and a
/// failed header slot write take only their database offline, and every later operation on it is
/// refused with CG06012 while nothing more is written to it.
/// </summary>
[Collection(nameof(GraphWorkerResilienceCollection))]
public sealed class GraphWorkerResilienceTests
{
    private const string failingDatabaseName = "failing";
    private const string healthyDatabaseName = "healthy";
    private static readonly TimeSpan _timeout = TimeSpan.FromSeconds(30);

    [Fact(DisplayName = "Cohesion Test [Database.Graph] - Workers: a checkpoint whose page writes fail is retried after a backoff, and recovers when the fault clears")]
    public async Task CheckpointWorker_PageWritesFail_ShouldRetryAndRecoverWhileOtherDatabasesCheckpoint()
    {
        // Arrange: the checkpointer looks every 100 ms; nothing else writes pages back.
        var strategy = new FaultInjectingJournalStorageStrategy(durable: true);
        await using var engine = GraphDatabaseEngine.Create("graph-engine", Options(strategy, checkpoint: TimeSpan.FromMilliseconds(100)));
        var failing = await engine.CreateDatabaseAsync(failingDatabaseName);
        var healthy = await engine.CreateDatabaseAsync(healthyDatabaseName);
        var worker = WorkerOf(engine, DatabaseEngineWorkerKind.Checkpoint);
        var workers = engine.Workers.ToArray();
        var faults = strategy.Faults(failingDatabaseName);

        // Act: every data page write of one database fails while its journal is due.
        faults.FailPageWrites = true;
        var during = Stopwatch.StartNew();
        await InsertAsync(failing, 100, 20);
        bool retried = await Eventually(() => worker.ConsecutiveFailures >= 2);
        var stateDuringTheFault = engine.State;
        var fault = worker.Fault;

        // The other database's checkpoints go on meanwhile.
        await InsertAsync(healthy, 100, 20);
        long healthyWritten = healthy.DataStorage.JournalLength;
        bool healthyCheckpointed = await Eventually(() => healthy.DataStorage.JournalLength < healthyWritten);
        long failedPasses = worker.FailureCount;
        var elapsed = during.Elapsed;
        long failingJournal = failing.DataStorage.JournalLength;

        faults.FailPageWrites = false;
        bool recovered = await Eventually(() => engine.State == EngineState.Running && failing.DataStorage.JournalLength < failingJournal);

        // Assert
        retried.ShouldBeTrue();
        stateDuringTheFault.ShouldBe(EngineState.Faulted);
        Mentions(fault, "Injected page write failure").ShouldBeTrue(fault?.ToString());
        healthyCheckpointed.ShouldBeTrue();
        ((double)failedPasses).ShouldBeLessThanOrEqualTo(elapsed / DatabaseEngineWorker.FailureBackoff + 2);
        recovered.ShouldBeTrue();
        worker.Fault.ShouldBeNull();
        worker.ConsecutiveFailures.ShouldBe(0);
        engine.Workers.ShouldBe(workers);
        engine.OfflineDatabases.ShouldBeEmpty();
        (await CountAsync(failing)).ShouldBe(20);
    }

    /// <summary>
    /// Every pass visits the healthy database while another database fails and backs off.
    /// Requested waits and due-state evidence distinguish a database backoff from a pump or lane
    /// slowdown; completed worker visits prove progress independently of statement checkpoints (#1284).
    /// </summary>
    /// <param name="observerPauseMilliseconds">Injected scheduler delay in the trigger observer.</param>
    [Theory(DisplayName = "Cohesion Test [Database.Graph] - Workers: a failing database leaves every healthy pass unthrottled")]
    [InlineData(0)]
    [InlineData(150)]
    public async Task CheckpointWorker_OneDatabaseKeepsFailing_ShouldKeepTheOthersAtFullPace(int observerPauseMilliseconds)
    {
        // Arrange: observe the actual engine pump and its database lanes, without changing waits.
        var strategy = new FaultInjectingJournalStorageStrategy(durable: true);
        var options = Options(strategy, checkpoint: TimeSpan.FromMilliseconds(100));
        options.CheckpointJournalSize = paceJournalSize;
        await using var engine = GraphDatabaseEngine.Create("pace-engine", options);
        var failing = await engine.CreateDatabaseAsync(failingDatabaseName);
        var healthy = await engine.CreateDatabaseAsync(healthyDatabaseName);
        var worker = (GraphCheckpointWorker)WorkerOf(engine, DatabaseEngineWorkerKind.Checkpoint);
        // Act / Assert: all five engines use the same per-visit progress proof.
        await CheckpointPace.VerifyAsync(worker, engine.CheckpointNeededSignal, failing.DataStorage, healthy.DataStorage,
            failingDatabaseName, healthyDatabaseName, strategy.Faults(failingDatabaseName),
            id => InsertAsync(failing, id, 10), id => InsertAsync(healthy, id, 10), paceJournalSize, observerPauseMilliseconds);
    }

    /// <summary>
    /// One database's checkpoint that hangs in a durable flush of its data file — an fsync its
    /// device does not answer — must not hold back the checkpoints of the engine's other databases.
    /// Before the checkpoint lanes, the worker visited the databases one by one on its own thread, so
    /// the hung fsync stopped every checkpoint of the engine until it returned: a device that took
    /// seconds to answer, or to fail, stalled every other database's journal truncation for as long.
    /// </summary>
    [Theory(DisplayName = "Cohesion Test [Database.Graph] - Workers: a database whose checkpoint fsync hangs does not hold back the other database's checkpoints")]
    [InlineData(0)]
    [InlineData(6000)]
    public async Task CheckpointWorker_OneDatabaseFsyncHangs_ShouldKeepCheckpointingTheOthers(int producerPauseMilliseconds)
    {
        // Arrange: checkpoints by journal size; nothing else writes pages back.
        var strategy = new FaultInjectingJournalStorageStrategy(durable: true);
        var options = Options(strategy);
        options.CheckpointJournalSize = paceJournalSize;
        await using var engine = GraphDatabaseEngine.Create("graph-engine", options);
        var stalled = await engine.CreateDatabaseAsync(failingDatabaseName);
        var healthy = await engine.CreateDatabaseAsync(healthyDatabaseName);
        var worker = WorkerOf(engine, DatabaseEngineWorkerKind.Checkpoint);
        var stalledFaults = strategy.Faults(failingDatabaseName);
        var healthyFaults = strategy.Faults(healthyDatabaseName);

        try
        {
            // The stalled database's journal grows past the size with its trigger off, so no
            // statement takes its checkpoint over: the worker runs it, and hangs in its data fsync.
            stalled.DataStorage.CheckpointJournalSize = 0;
            for (int id = 0; stalled.DataStorage.JournalLength < paceJournalSize; id += 10)
            {
                await InsertAsync(stalled, id, 10);
            }

            stalledFaults.StallDataFlushes();
            stalled.DataStorage.CheckpointJournalSize = paceJournalSize;
            bool hung = await Eventually(() => stalledFaults.StalledDataFlushes > 0);
            long stalledJournal = stalled.DataStorage.JournalLength;

            // Act: while the fsync hangs, the healthy database's journal passes the size again and again.
            long checkpoints = healthyFaults.HeaderWrites;
            var watch = Stopwatch.StartNew();
            // Simulate descheduling after observation starts, before producing due work.
            await Task.Delay(producerPauseMilliseconds);
            for (int id = 0; healthyFaults.HeaderWrites - checkpoints < 3 && watch.Elapsed < _timeout; id += 10)
            {
                await InsertAsync(healthy, id, 10);
            }

            long healthyCheckpoints = healthyFaults.HeaderWrites - checkpoints;
            bool stillHung = stalledFaults.StalledDataFlushes > 0;
            long stalledJournalDuringTheHang = stalled.DataStorage.JournalLength;

            stalledFaults.ReleaseDataFlushes();
            bool stalledCheckpointed = await Eventually(() => stalled.DataStorage.JournalLength < stalledJournal && worker.Fault is null);

            // Assert: the healthy database was checkpointed while the other's checkpoint hung, and
            // the hung checkpoint completed once its device answered.
            hung.ShouldBeTrue();
            healthyCheckpoints.ShouldBeGreaterThanOrEqualTo(3, $"healthy checkpoints while the other database's fsync hung for {watch.Elapsed}");
            stillHung.ShouldBeTrue();
            stalledJournalDuringTheHang.ShouldBe(stalledJournal);
            stalledCheckpointed.ShouldBeTrue();
            worker.Fault.ShouldBeNull();
            engine.State.ShouldBe(EngineState.Running);
            engine.OfflineDatabases.ShouldBeEmpty();
        }
        finally
        {
            stalledFaults.Clear();
        }
    }

    [Fact(DisplayName = "Cohesion Test [Database.Graph] - Workers: a page write-back that fails is retried after a backoff, and the pages reach the file once the fault clears")]
    public async Task PageWriteBackWorker_PageWritesFail_ShouldRetryAndRecoverWhileOtherDatabasesAreWritten()
    {
        // Arrange: the page writer runs every 50 ms; no checkpoint writes pages.
        var strategy = new FaultInjectingJournalStorageStrategy(durable: true);
        await using var engine = GraphDatabaseEngine.Create("graph-engine", Options(strategy, writeBack: TimeSpan.FromMilliseconds(50)));
        var failing = await engine.CreateDatabaseAsync(failingDatabaseName);
        var healthy = await engine.CreateDatabaseAsync(healthyDatabaseName);
        var worker = WorkerOf(engine, DatabaseEngineWorkerKind.PageWriteBack);
        var faults = strategy.Faults(failingDatabaseName);
        var healthyFaults = strategy.Faults(healthyDatabaseName);

        // Act: the failing database's page writes fail while it has dirty pages.
        faults.FailPageWrites = true;
        var during = Stopwatch.StartNew();
        await InsertAsync(failing, 100, 20);
        bool retried = await Eventually(() => worker.ConsecutiveFailures >= 2);
        var stateDuringTheFault = engine.State;
        var fault = worker.Fault;

        // The other database's pages are written back meanwhile.
        long healthyWrites = healthyFaults.PageWrites;
        await InsertAsync(healthy, 100, 20);
        bool healthyWritten = await Eventually(() => healthyFaults.PageWrites > healthyWrites);
        long failedPasses = worker.FailureCount;
        var elapsed = during.Elapsed;

        var atTheClear = strategy.Capture(failingDatabaseName).Data;
        faults.FailPageWrites = false;
        bool recovered = await Eventually(() =>
            engine.State == EngineState.Running && !strategy.Capture(failingDatabaseName).Data.AsSpan().SequenceEqual(atTheClear));

        // Assert
        retried.ShouldBeTrue();
        stateDuringTheFault.ShouldBe(EngineState.Faulted);
        Mentions(fault, "Injected page write failure").ShouldBeTrue(fault?.ToString());
        healthyWritten.ShouldBeTrue();
        ((double)failedPasses).ShouldBeLessThanOrEqualTo(elapsed / DatabaseEngineWorker.FailureBackoff + 2);
        recovered.ShouldBeTrue();
        worker.Fault.ShouldBeNull();
        engine.OfflineDatabases.ShouldBeEmpty();
        (await CountAsync(failing)).ShouldBe(20);
    }

    /// <summary>
    /// The flush worker's group flush fails in either of its two steps: the drain of the journal's
    /// append buffer (#1252) or the fsync after it (#1243). Either takes only its database offline
    /// with <see cref="StorageOfflineCause.JournalFlush"/>, on the worker's thread, while the worker
    /// records no failure of its own and keeps flushing the other database (#1268).
    /// </summary>
    [Theory(DisplayName = "Cohesion Test [Database.Graph] - Workers: a group-commit drain or fsync failure takes only its database offline, and the flush worker keeps serving the others")]
    [InlineData(DeviceFault.JournalFlush, 0)]
    [InlineData(DeviceFault.JournalWrite, 0)]
    [InlineData(DeviceFault.JournalFlush, 2100)]
    public async Task WriteAheadFlushWorker_JournalFails_ShouldTakeOnlyItsDatabaseOfflineAndKeepFlushing(DeviceFault fault, int completionPauseMilliseconds)
    {
        // Arrange: event 8 proves a worker flush; event 9 identifies committer self-help.
        // Construction keeps its short window, then commits get a generous hang guard.
        var window = TimeSpan.FromSeconds(2);
        var strategy = new FaultInjectingJournalStorageStrategy(durable: true);
        var options = Options(strategy);
        options.Durability = StorageCommitDurability.Grouped;
        options.GroupCommitWindow = window;
        await using var engine = GraphDatabaseEngine.Create("graph-engine", options);
        var failing = await engine.CreateDatabaseAsync(failingDatabaseName);
        var healthy = await engine.CreateDatabaseAsync(healthyDatabaseName);
        failing.DataStorage.GroupCommitWindow = healthy.DataStorage.GroupCommitWindow = _timeout + TimeSpan.FromSeconds(5);
        using var commits = new EventSourceRecorder(EventSource.GetSources().Single(source =>
            source.Name == "Assimalign.Cohesion.Database.Storage"), EventLevel.Verbose);
        int EventsFor(string database, int id) => commits.Events.Count(item => item.EventId == id
            && item.Payload is { Count: > 0 } payload
            && (Equals(payload[0], database) || Equals(payload[0], database + "/catalog")));
        var worker = WorkerOf(engine, DatabaseEngineWorkerKind.WriteAheadFlush);
        var faults = strategy.Faults(failingDatabaseName);
        await using var session = await failing.CreateSessionAsync();

        // Act: the failing database's journal drain, or its fsync, fails under the worker's group
        // flush.
        faults.SwitchOn(fault);
        var error = await Record.ExceptionAsync(async () => await session.ExecuteAsync("INSERT (:Item {name: 'lost'})"));
        // Exercise a caller continuation delayed by a busy runner after the worker has answered.
        await Task.Delay(completionPauseMilliseconds);
        var refusal = await Should.ThrowAsync<DatabaseOfflineException>(async () => await failing.CreateSessionAsync());

        // The worker keeps flushing the other database's grouped commits.
        await WriteGroupedInsertsAsync(healthy, 5);
        // The gate wakes committers before recording event 8; completed inserts alone do not
        // prove that the recorder has observed every flush. The timeout is only a hang guard.
        (await Eventually(() => EventsFor(healthyDatabaseName, 8) >= 5)).ShouldBeTrue(
            "the recorder must observe all five worker flushes before their count is captured");

        int failingSelfHelp = EventsFor(failingDatabaseName, 9);
        int healthyFlushes = EventsFor(healthyDatabaseName, 8);
        int healthySelfHelp = EventsFor(healthyDatabaseName, 9);
        faults.Clear();
        var reopened = await engine.OpenDatabaseAsync(failingDatabaseName);

        // Assert: a failed drain ends the group flush before its fsync.
        bool drain = fault == DeviceFault.JournalWrite;
        StorageOfflineException.Find(error.ShouldNotBeNull()).ShouldNotBeNull();
        failingSelfHelp.ShouldBe(0, "the failed flush must be worker-owned");
        (drain ? faults.JournalWriteFailures : faults.JournalFlushFailures).ShouldBeGreaterThanOrEqualTo(1);

        // The root engine base names each pump thread for its worker (concrete-types plan §6.4,
        // engine composition), where the model named it "{engine}/{kind}" ("graph-engine/WriteAheadFlush").
        (drain ? faults.JournalWriteFailureThread : faults.JournalFlushFailureThread).ShouldBe(worker.Name + " lane");
        worker.Name.ShouldBe(engine.Name + "/wal-flush");
        (drain ? faults.JournalFlushFailures : faults.JournalWriteFailures).ShouldBe(0);
        refusal.Code.ShouldBe("CG06012");
        StorageOfflineException.Find(refusal)!.Cause.ShouldBe(StorageOfflineCause.JournalFlush);
        refusal.Message.ShouldContain("a write or flush of the journal");
        StorageOfflineException.Find(refusal)!.Message.ShouldContain(drain ? "a write of the journal" : "a durable flush of the journal");
        healthyFlushes.ShouldBeGreaterThanOrEqualTo(5);
        healthySelfHelp.ShouldBe(0, "healthy grouped commits must be flushed by the worker");
        worker.Fault.ShouldBeNull();
        worker.FailureCount.ShouldBe(0);
        engine.State.ShouldBe(EngineState.Running);
        reopened.IsOffline.ShouldBeFalse();
        engine.OfflineDatabases.ShouldBeEmpty();
    }

    [Fact(DisplayName = "Cohesion Test [Database.Graph] - Workers: a failed header slot write takes the database offline: later operations are refused and its files stop growing")]
    public async Task CheckpointWorker_HeaderWriteFails_ShouldTakeTheDatabaseOfflineUntilReopened()
    {
        // Arrange
        var strategy = new FaultInjectingJournalStorageStrategy(durable: true);
        var options = Options(strategy);
        options.CheckpointJournalSize = 0;
        await using var engine = GraphDatabaseEngine.Create("graph-engine", options);
        var failing = await engine.CreateDatabaseAsync(failingDatabaseName);
        var healthy = await engine.CreateDatabaseAsync(healthyDatabaseName);
        await InsertAsync(failing, 0, 10);
        var worker = WorkerOf(engine, DatabaseEngineWorkerKind.Checkpoint);
        var faults = strategy.Faults(failingDatabaseName);
        await using var session = await failing.CreateSessionAsync();
        await session.ExecuteAsync("INSERT (:Item {name: 'k10'})");

        // Act: all writes have committed before the worker can reach the injected header fault.
        // The hour-long time trigger stays out of this test; enabling the size trigger wakes it.
        faults.FailHeaderWrites = true;
        failing.DataStorage.CheckpointJournalSize = 1;
        bool offline = await Eventually(() => faults.HeaderWriteFailures == 1 && engine.OfflineDatabases.Contains((DatabaseName)failingDatabaseName));
        long journalAtTheFault = failing.DataStorage.JournalLength;
        var atTheFault = strategy.Capture(failingDatabaseName);

        var refusals = new List<DatabaseOfflineException>
        {
            await Should.ThrowAsync<DatabaseOfflineException>(async () => await failing.CreateSessionAsync()),
            await Should.ThrowAsync<DatabaseOfflineException>(async () => await session.ExecuteAsync("INSERT (:Item {name: 'k11'})")),
            await Should.ThrowAsync<DatabaseOfflineException>(async () => await session.BeginTransactionAsync()),
        };

        // The checkpoint worker keeps visiting the engine: the healthy database is checkpointed.
        await InsertAsync(healthy, 0, 10);
        long healthyWritten = healthy.DataStorage.JournalLength;
        healthy.DataStorage.CheckpointJournalSize = 1;
        bool healthyCheckpointed = await Eventually(() => healthy.DataStorage.JournalLength < healthyWritten);
        var after = strategy.Capture(failingDatabaseName);

        faults.Clear();
        var reopened = await engine.OpenDatabaseAsync(failingDatabaseName);

        // Assert
        offline.ShouldBeTrue();
        refusals.ShouldAllBe(refusal => refusal.Code == "CG06012" && refusal.Diagnostics.Count == 1 && refusal.Diagnostics[0].Code == "CG06012" && refusal.Message.StartsWith("Database", StringComparison.Ordinal));
        refusals.ShouldAllBe(refusal => refusal.Message.Contains("a write of the file header", StringComparison.Ordinal));
        StorageOfflineException.Find(refusals[0])!.Cause.ShouldBe(StorageOfflineCause.HeaderWrite);
        faults.HeaderWriteFailures.ShouldBe(1);
        failing.DataStorage.JournalLength.ShouldBe(journalAtTheFault);
        after.Data.ShouldBe(atTheFault.Data);
        after.Journal.ShouldBe(atTheFault.Journal);
        healthyCheckpointed.ShouldBeTrue();
        worker.Fault.ShouldBeNull();
        worker.FailureCount.ShouldBe(0);
        engine.State.ShouldBe(EngineState.Running);
        reopened.IsOffline.ShouldBeFalse();
        (await CountAsync(reopened)).ShouldBe(11);
    }

    /// <summary>
    /// Two writers in flight when a failed header slot write, journal fsync or journal drain takes
    /// the database offline (#1268 review): the writer holding the database writer lock keeps it,
    /// because an offline database undoes nothing and releasing the lock without the undo would
    /// hand the next writer versions that were never undone, so the writer queued behind it must
    /// end with the coded refusal instead. Before the review it waited until the database was
    /// reopened. The drain of the journal's append buffer (#1252) goes offline through the same
    /// hook, so it ends the wait too, with <see cref="StorageOfflineCause.JournalFlush"/> as the
    /// cause.
    /// </summary>
    /// <remarks>
    /// Before the fault switches on, the test checks that the queued writer's transaction is open
    /// beside the holder's and its statement has not completed, on a database still online, as
    /// the blob engine's test checks; a fixed wait of 100 ms only assumed it. The check does not
    /// show that the writer reached the lock, since the lock manager cannot be asked about its
    /// waiters, and a writer still on its way there ends the same way: once the database is
    /// offline a lock wait is refused as it begins (<c>AbandonLockWaits</c>), with the same coded
    /// refusal.
    /// </remarks>
    [Theory(DisplayName = "Cohesion Test [Database.Graph] - Workers: a writer queued for the writer lock when the database goes offline ends with the coded refusal")]
    [InlineData(DeviceFault.HeaderWrite)]
    [InlineData(DeviceFault.JournalFlush)]
    [InlineData(DeviceFault.JournalWrite)]
    public async Task QueuedWriter_DatabaseGoesOffline_ShouldEndWithTheOfflineRefusal(DeviceFault fault)
    {
        // Arrange: the checkpointer looks every 100 ms; one writer holds the database writer lock in
        // an explicit transaction, and another queues behind it.
        var strategy = new FaultInjectingJournalStorageStrategy(durable: true);
        await using var engine = GraphDatabaseEngine.Create("graph-engine", Options(strategy, checkpoint: TimeSpan.FromMilliseconds(100)));
        var failing = await engine.CreateDatabaseAsync(failingDatabaseName);
        var faults = strategy.Faults(failingDatabaseName);
        await using var holder = await failing.CreateSessionAsync();
        await using var queued = await failing.CreateSessionAsync();
        _ = await holder.BeginTransactionAsync();
        await holder.ExecuteAsync("INSERT (:Item {name: 'held'})");
        var waiting = queued.ExecuteAsync("INSERT (:Item {name: 'queued'})").AsTask();

        // The queued writer's transaction is open beside the holder's, and its statement has not
        // completed, on a database still online. The ReadCommitted autocommit statement also
        // owns a snapshot-retention pin (#1149), so there are three contexts.
        await Eventually(() => waiting.IsCompleted || failing.Coordinator.GetOpenContexts().Count == 3);
        bool queuedWhileOnline = !waiting.IsCompleted && failing.Coordinator.GetOpenContexts().Count == 3 && !failing.IsOffline;
        string queuedState = $"queued {waiting.Status}, {failing.Coordinator.GetOpenContexts().Count} open contexts, offline {failing.IsOffline}";

        // Act: the next checkpoint's header slot write, its journal fsync, or the drain of the
        // journal's append buffer that leads it, fails.
        faults.SwitchOn(fault);

        // The holder writes again, so the next checkpoint is due (it may already be refused).
        await Record.ExceptionAsync(async () => await holder.ExecuteAsync("INSERT (:Item {name: 'held-2'})"));
        bool offline = await Eventually(() => engine.OfflineDatabases.Contains((DatabaseName)failingDatabaseName));
        bool ended = await Task.WhenAny(waiting, Task.Delay(TimeSpan.FromSeconds(5))) == waiting;
        var queuedRefusal = ended ? await Record.ExceptionAsync(() => waiting) : null;
        var holderRefusal = await Record.ExceptionAsync(async () => await holder.ExecuteAsync("INSERT (:Item {name: 'after'})"));

        faults.Clear();
        var reopened = await engine.OpenDatabaseAsync(failingDatabaseName);

        // Assert: both writers got the coded refusal naming what failed, and the reopen kept
        // neither write.
        queuedWhileOnline.ShouldBeTrue(queuedState);
        offline.ShouldBeTrue();
        ended.ShouldBeTrue("the queued writer was still waiting five seconds after the database went offline");
        queuedRefusal.ShouldBeOfType<DatabaseOfflineException>().Code.ShouldBe("CG06012");
        StorageOfflineException.Find(queuedRefusal)!.Cause.ShouldBe(fault == DeviceFault.HeaderWrite ? StorageOfflineCause.HeaderWrite : StorageOfflineCause.JournalFlush);
        holderRefusal.ShouldBeOfType<DatabaseOfflineException>().Code.ShouldBe("CG06012");
        reopened.IsOffline.ShouldBeFalse();
        (await CountAsync(reopened)).ShouldBe(0);
    }

    // Since the engine derives from DatabaseEngine (concrete-types plan, phase 4), a registered
    // worker is a DatabaseEngineWorker, whose loop lets nothing escape: the pass that throws is
    // recorded and the next one runs after the backoff. The interface-only worker this test drove
    // until then cannot be registered any more.
    [Fact(DisplayName = "Cohesion Test [Database.Graph] - Workers: a registered worker whose pass throws is run again, and the engine reports Faulted")]
    public async Task Pump_RegisteredWorkerPassThrows_ShouldRunItAgainAndReportFaulted()
    {
        // Arrange
        var worker = new EscapingWorker();
        var builder = GraphDatabaseEngine.CreateBuilder("graph-engine");
        builder.AddWorker(_ => worker);
        await using var engine = builder.Build();

        // Act
        bool restarted = await Eventually(() => worker.Runs >= 2);
        var state = engine.State;
        await engine.DisposeAsync();

        // Assert
        restarted.ShouldBeTrue();
        state.ShouldBe(EngineState.Faulted);
        worker.Stopped.ShouldBeTrue();
    }

    /// <summary>
    /// A database its holder closed outside the engine (disposed directly; a session's
    /// <see cref="GraphDatabaseSession.Database"/> is the same instance) leaves the engine's workers
    /// passing: every pass succeeds, no worker records a failure, the engine stays
    /// <see cref="EngineState.Running"/>, and a server over the engine still starts and serves the
    /// engine's other database. Once the close ends the engine forgets the database, and the next
    /// open opens it again from its files with its nodes (owner decision 33, #1289). Before the
    /// workers skipped a closed database, the version-purge worker failed on its disposed
    /// coordinator every pass, and the engine reported <see cref="EngineState.Faulted"/> for good;
    /// until decision 33 the engine refused the reopen with <see cref="ObjectDisposedException"/>
    /// until the database was dropped.
    /// </summary>
    [Fact(DisplayName = "Cohesion Test [Database.Graph] - Workers: a database closed outside the engine is skipped, the engine stays running and its server serves")]
    public async Task DisposeAsync_DatabaseClosedOutsideTheEngine_ShouldLeaveTheEngineRunningAndItsServerServing()
    {
        // Arrange: workers that pass every 20 ms, and a closed database with written nodes.
        using var deadline = new CancellationTokenSource(_timeout);
        CancellationToken token = deadline.Token;
        var interval = TimeSpan.FromMilliseconds(20);
        await using var engine = GraphDatabaseEngine.Create("graph-engine", new()
        {
            CheckpointInterval = interval, PageWriteBackInterval = interval, MaintenanceInterval = interval
        });
        var closed = await engine.CreateDatabaseAsync(failingDatabaseName, token);
        await InsertAsync(closed, 0, 20);
        var open = await engine.CreateDatabaseAsync(healthyDatabaseName, token);

        // Act: close the database, let the workers pass over it many times, run one more pass of
        // each, then start a server and write over it to the other database.
        await closed.DisposeAsync();
        await Task.Delay(TimeSpan.FromMilliseconds(500), token);
        var passes = engine.Workers.Select(worker => worker.RunIteration(token)).ToArray();
        var listener = new InMemoryConnectionListener();
        await using var server = GraphDatabaseServer.Create(engine, new() { Listener = listener });
        await server.StartAsync(token);
        await using IDuplexConnection connection = await listener.CreateFactory().ConnectAsync(listener.EndPoint, token);
        await using var channel = new ProtocolChannel(connection.AsStream(), GraphProtocol.Family, leaveOpen: true);
        await WriteFrameAsync(channel, ProtocolMessageType.Startup, new ProtocolStartupMessage(ProtocolVersion.Current, healthyDatabaseName, "test").Encode(), token);
        (await ReadFrameAsync(channel, token)).Type.ShouldBe(ProtocolMessageType.Authenticate);
        await WriteFrameAsync(channel, ProtocolMessageType.AuthenticateResponse, [], token);
        (await ReadFrameAsync(channel, token)).Type.ShouldBe(ProtocolMessageType.Ready);
        await WriteFrameAsync(channel, (ProtocolMessageType)GraphProtocolMessageType.Execute,
            GraphProtocolExecuteMessage.Create("INSERT (:Item {name: 'wire'})").Encode(), token);
        var written = await ReadFrameAsync(channel, token);

        // Assert
        passes.ShouldAllBe(passed => passed);
        engine.Workers.Where(worker => worker.FailureCount != 0 || worker.Fault is not null)
            .Select(worker => $"{worker.Name}: {worker.FailureCount} failed passes, {worker.Fault}").ShouldBeEmpty();
        engine.State.ShouldBe(EngineState.Running);
        engine.OfflineDatabases.ShouldBeEmpty();
        server.Sessions.ShouldHaveSingleItem().DatabaseSession.ShouldNotBeNull().Database.Name.ShouldBe(open.Name);
        written.Type.ShouldBe((ProtocolMessageType)GraphProtocolMessageType.ResultComplete);
        (await CountAsync(open)).ShouldBe(1);
        engine.TryGetDatabase(failingDatabaseName, out _).ShouldBeFalse();
        var reopened = await engine.OpenDatabaseAsync(failingDatabaseName, token);
        reopened.ShouldNotBeSameAs(closed);
        (await CountAsync(reopened)).ShouldBe(20);
    }

    /// <summary>
    /// A database a holder closed outside the engine is forgotten once its close ends, so the next
    /// open opens it again, a new instance with every node it held, in memory as on disk (owner
    /// decision 33, #1289; #1272's in-memory reopen): the engine keeps an in-memory database's
    /// files for its own lifetime, and the open runs recovery over them as it does over files. A
    /// node of a transaction the close ended is not there. The reopened database takes writes, and
    /// a second close and open keeps them too. Until decision 33 the engine refused the reopen with
    /// <see cref="ObjectDisposedException"/> until the database was dropped.
    /// </summary>
    /// <param name="onDisk">True for a file-backed engine; false for an in-memory one.</param>
    /// <param name="throughSession">Whether the database is closed through a session's database rather than directly.</param>
    [Theory(DisplayName = "Cohesion Test [Database.Graph] - Lifecycle: a database closed outside the engine reopens with its nodes, in memory and on disk")]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task OpenDatabaseAsync_DatabaseClosedOutsideTheEngine_ShouldReopenItWithItsNodes(bool onDisk, bool throughSession)
    {
        // Arrange: a database with committed nodes, and one more in an uncommitted transaction.
        string root = Path.Combine(Path.GetTempPath(), "cohesion-graph-reopen-" + Guid.NewGuid().ToString("N"));
        try
        {
            await using var engine = GraphDatabaseEngine.Create("graph-engine", onDisk ? new() { RootPath = root } : new());
            var database = await engine.CreateDatabaseAsync(failingDatabaseName);
            await InsertAsync(database, 0, 20);
            var session = await database.CreateSessionAsync();
            _ = await session.BeginTransactionAsync();
            await session.ExecuteAsync("INSERT (:Item {name: 'uncommitted'})");

            // Act: close the database outside the engine, then open it again, write to it, and
            // close and open it once more.
            if (throughSession)
            {
                await session.Database.DisposeAsync();
            }
            else
            {
                await database.DisposeAsync();
            }

            bool foundAfterTheClose = engine.TryGetDatabase(failingDatabaseName, out _);
            var reopened = await engine.OpenDatabaseAsync(failingDatabaseName);
            int afterTheReopen = await CountAsync(reopened);
            await InsertAsync(reopened, 20, 5);
            await reopened.DisposeAsync();
            var again = await engine.OpenDatabaseAsync(failingDatabaseName);

            // Assert
            foundAfterTheClose.ShouldBeFalse();
            reopened.ShouldNotBeSameAs(database);
            afterTheReopen.ShouldBe(20);
            again.ShouldNotBeSameAs(reopened);
            (await CountAsync(again)).ShouldBe(25);
            engine.State.ShouldBe(EngineState.Running);
            await session.DisposeAsync();
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    /// <summary>
    /// The checkpointer skips a database its holder closed, rather than only tolerating its
    /// disposed storage. A close that is not idle leaves the journal untruncated: here another
    /// storage bracket holds every page through the close, so the rolled-back transaction's
    /// deferred undo still fails when the close retries it, and the close keeps its writer in
    /// flight (#1226). The closed storage then stays due for a checkpoint that can never run.
    /// Before the skip, when the checkpointer already had a failure recorded for the database, every
    /// poll handed a lane that checkpoint, its refusal (<see cref="StorageTransactionException"/>)
    /// kept the failure recorded, and the engine reported <see cref="EngineState.Faulted"/> for good.
    /// Now the model's <c>IsCheckpointDue</c> is false for a closed database, so the first pass after
    /// the failure's backoff ends the record.
    /// </summary>
    [Fact(DisplayName = "Cohesion Test [Database.Graph] - Workers: a failing database closed with a writer in flight ends its checkpoint failure, and the engine runs again")]
    public async Task CheckpointWorker_FailingDatabaseClosedWithAWriterInFlight_ShouldEndItsFailureAndLeaveTheEngineRunning()
    {
        // Arrange: the checkpointer looks every 100 ms and records a failure for a database whose
        // page writes fail.
        var strategy = new FaultInjectingJournalStorageStrategy(durable: true);
        await using var engine = GraphDatabaseEngine.Create("graph-engine", Options(strategy, checkpoint: TimeSpan.FromMilliseconds(100)));
        var failing = await engine.CreateDatabaseAsync(failingDatabaseName);
        var worker = WorkerOf(engine, DatabaseEngineWorkerKind.Checkpoint);
        var faults = strategy.Faults(failingDatabaseName);
        faults.FailPageWrites = true;
        await InsertAsync(failing, 0, 20);
        bool failed = await Eventually(() => worker.ConsecutiveFailures >= 1);

        // A rolled-back transaction whose undo is deferred: another bracket holds every page
        // through the close, so no checkpoint of the storage can run from here on either.
        await using (var session = await failing.CreateSessionAsync())
        {
            var transaction = await session.BeginTransactionAsync();
            await session.ExecuteAsync("INSERT (:Item {name: 'rolled'})");
            faults.FailPageWrites = false;
            _ = PageWriteLockHolder.LockEveryPage(failing.DataStorage); // abandoned with the storage
            await transaction.RollbackAsync();
        }

        // Act: the close retries the undo, which fails the same way, and keeps the writer in flight.
        await Should.ThrowAsync<StorageTransactionException>(async () => await failing.DisposeAsync());
        bool recovered = await Eventually(() => engine.State == EngineState.Running && worker.Fault is null);

        // Assert
        failed.ShouldBeTrue();
        recovered.ShouldBeTrue($"{engine.State}: {worker.Name} {worker.ConsecutiveFailures} consecutive failed passes, {worker.Fault}");
        worker.ConsecutiveFailures.ShouldBe(0);
        engine.OfflineDatabases.ShouldBeEmpty();
    }

    /// <summary>
    /// A checkpoint failure that persists takes the database offline (owner decisions 25 of
    /// 2026-10-06 and 42 of 2026-10-07): once the checkpoint worker's failures of it have lasted the
    /// engine's window across its minimum of failed passes, the engine takes it offline with
    /// <see cref="StorageOfflineCause.CheckpointFailures"/>, and only it: the other database stays
    /// online and keeps being checkpointed. The window is measured on the engine's clock, which the
    /// test moves: the minimum of failed passes, each a backoff after the one before it, leaves the
    /// database online while the window has not passed, and the first failed pass past it takes the
    /// database offline. Every operation is then refused with CG06012, and once the fault clears the
    /// reopen brings the database back with its nodes. Before decision 25 the worker retried it
    /// every second for as long as the process ran, and its journal grew without bound.
    /// </summary>
    [Fact(DisplayName = "Cohesion Test [Database.Graph] - Workers: a checkpoint failure that persists past the window across the minimum of passes takes only its database offline")]
    public async Task CheckpointWorker_FailurePersistsPastTheWindow_ShouldTakeOnlyItsDatabaseOffline()
    {
        // Arrange: the checkpointer looks every 100 ms; the default window of 100 s and minimum of
        // three failed passes, on a clock the test moves.
        var clock = new ManualTimeProvider();
        var strategy = new FaultInjectingJournalStorageStrategy(durable: true);
        var options = Options(strategy, checkpoint: TimeSpan.FromMilliseconds(100));
        options.WorkerFailureWindow = DatabaseEngine.DefaultWorkerFailureWindow;
        options.WorkerFailureMinimumPasses = DatabaseEngine.DefaultWorkerFailureMinimumPasses;
        options.TimeProvider = clock;
        await using var engine = GraphDatabaseEngine.Create("graph-engine", options);
        var failing = await engine.CreateDatabaseAsync(failingDatabaseName);
        var healthy = await engine.CreateDatabaseAsync(healthyDatabaseName);
        var worker = WorkerOf(engine, DatabaseEngineWorkerKind.Checkpoint);
        var faults = strategy.Faults(failingDatabaseName);

        // Act: every data page write of one database fails while its journal is due, for good. The
        // minimum of failed passes, a backoff apart, passes inside the window.
        faults.FailPageWrites = true;
        await InsertAsync(failing, 0, 20);
        bool failedTheMinimum = await Eventually(() => worker.FailureCount >= DatabaseEngine.DefaultWorkerFailureMinimumPasses);
        bool onlineInsideTheWindow = !failing.IsOffline && engine.OfflineDatabases.Count == 0;
        long passesInsideTheWindow = worker.FailureCount;

        // The window passes on the engine's clock; the next failed pass gives up on the database.
        clock.Advance(DatabaseEngine.DefaultWorkerFailureWindow);
        bool offline = await Eventually(() => engine.OfflineDatabases.Contains((DatabaseName)failingDatabaseName));
        var refusal = await Should.ThrowAsync<DatabaseOfflineException>(async () => await failing.CreateSessionAsync());
        var error = engine.GetOfflineError(failingDatabaseName);

        // The checkpointer keeps serving the other database, and the failure record ends.
        await InsertAsync(healthy, 0, 20);
        long healthyWritten = healthy.DataStorage.JournalLength;
        bool healthyCheckpointed = await Eventually(() => healthy.DataStorage.JournalLength < healthyWritten);
        bool running = await Eventually(() => worker.Fault is null && engine.State == EngineState.Running);
        long failedPasses = worker.FailureCount;
        var offlineList = engine.OfflineDatabases.ToArray();

        faults.Clear();
        var reopened = await engine.OpenDatabaseAsync(failingDatabaseName);

        // Assert: online through the minimum of passes inside the window, offline on a failed pass
        // past it.
        failedTheMinimum.ShouldBeTrue();
        onlineInsideTheWindow.ShouldBeTrue();
        offline.ShouldBeTrue();
        failedPasses.ShouldBeGreaterThan(passesInsideTheWindow);
        refusal.Code.ShouldBe("CG06012");
        refusal.Diagnostics.ShouldContain(d => d.Code == "CG06012");
        refusal.Message.ShouldStartWith("Database", Case.Sensitive);
        refusal.Message.ShouldContain("its checkpoints kept failing");
        var cause = StorageOfflineException.Find(refusal).ShouldNotBeNull();
        cause.Cause.ShouldBe(StorageOfflineCause.CheckpointFailures);
        cause.Message.ShouldContain($"'{worker.Name}'");
        cause.Message.ShouldContain("passes in a row over 100 s, at least the engine's window of 100 s", Case.Sensitive);
        Mentions(refusal, "Injected page write failure").ShouldBeTrue(refusal.ToString());
        error.ShouldNotBeNull().Cause.ShouldBe(StorageOfflineCause.CheckpointFailures);
        offlineList.ShouldBe([(DatabaseName)failingDatabaseName]);
        healthy.IsOffline.ShouldBeFalse();
        healthyCheckpointed.ShouldBeTrue();
        running.ShouldBeTrue();
        reopened.IsOffline.ShouldBeFalse();
        engine.OfflineDatabases.ShouldBeEmpty();
        (await CountAsync(reopened)).ShouldBe(20);
    }

    /// <summary>
    /// A journal that grows past the engine's cap while its checkpoints fail takes the database
    /// offline on its second failed checkpoint in a row (owner decision 25 and its review), long
    /// before the failure window, with
    /// <see cref="StorageOfflineCause.JournalSizeLimit"/>, and only that database: under load the
    /// journal would otherwise fill the device first.
    /// </summary>
    [Fact(DisplayName = "Cohesion Test [Database.Graph] - Workers: a journal past the cap while its checkpoints fail takes only its database offline")]
    public async Task CheckpointWorker_JournalPastTheCap_ShouldTakeOnlyItsDatabaseOffline()
    {
        // Arrange: checkpoints by journal size, a cap of four times the size, and a minimum of failed
        // passes out of reach.
        var strategy = new FaultInjectingJournalStorageStrategy(durable: true);
        var options = Options(strategy);
        options.CheckpointJournalSize = paceJournalSize;
        options.JournalSizeLimit = 4 * paceJournalSize;
        await using var engine = GraphDatabaseEngine.Create("graph-engine", options);
        var failing = await engine.CreateDatabaseAsync(failingDatabaseName);
        var healthy = await engine.CreateDatabaseAsync(healthyDatabaseName);
        var worker = WorkerOf(engine, DatabaseEngineWorkerKind.Checkpoint);
        var faults = strategy.Faults(failingDatabaseName);

        // Act: the failing database's journal grows past the cap while its checkpoints fail.
        faults.FailPageWrites = true;
        try
        {
            for (int id = 0; failing.DataStorage.JournalLength < options.JournalSizeLimit; id += 10)
            {
                await InsertAsync(failing, id, 10);
            }
        }
        catch (DatabaseOfflineException)
        {
            // A failed checkpoint found the journal past the cap between two writes.
        }

        bool offline = await Eventually(() => engine.OfflineDatabases.Contains((DatabaseName)failingDatabaseName));
        long failedPasses = worker.FailureCount;
        bool failedTwice = await Eventually(() => worker.FailureCount >= 2);
        var refusal = await Should.ThrowAsync<DatabaseOfflineException>(async () => await failing.CreateSessionAsync());

        // The other database is still checkpointed by size.
        long checkpoints = strategy.Faults(healthyDatabaseName).HeaderWrites;
        for (int id = 0; strategy.Faults(healthyDatabaseName).HeaderWrites == checkpoints && id < 100_000; id += 10)
        {
            await InsertAsync(healthy, id, 10);
        }

        // Assert
        offline.ShouldBeTrue();
        // Long before the window: fewer failed passes than the window holds at the backoff.
        failedPasses.ShouldBeLessThan((long)(DatabaseEngine.DefaultWorkerFailureWindow / DatabaseEngineWorker.FailureBackoff));
        failedTwice.ShouldBeTrue();
        refusal.Code.ShouldBe("CG06012");
        refusal.Message.ShouldContain("its journal grew past its engine's limit while its checkpoints kept failing");
        var cause = StorageOfflineException.Find(refusal).ShouldNotBeNull();
        cause.Cause.ShouldBe(StorageOfflineCause.JournalSizeLimit);
        cause.Message.ShouldContain($"past the engine's limit of {options.JournalSizeLimit} bytes");
        cause.Message.ShouldContain($"'{worker.Name}'");
        cause.Message.ShouldContain("passes in a row");
        engine.OfflineDatabases.ShouldBe([(DatabaseName)failingDatabaseName]);
        healthy.IsOffline.ShouldBeFalse();
        strategy.Faults(healthyDatabaseName).HeaderWrites.ShouldBeGreaterThan(checkpoints);
    }

    /// <summary>
    /// A journal that reached the engine's cap with no failure at all (a starved checkpointer,
    /// #1283) is not taken offline by one transient checkpoint failure (owner decision 25 review):
    /// the cap needs a second failed checkpoint in a row. Here the journal grows past the cap while
    /// no checkpoint is due, one checkpoint then fails, the fault clears before the worker's retry,
    /// and the retried checkpoint truncates the journal.
    /// </summary>
    [Fact(DisplayName = "Cohesion Test [Database.Graph] - Workers: one transient checkpoint failure of a journal already past the cap leaves the database online")]
    public async Task CheckpointWorker_JournalPastTheCapOneTransientFailure_ShouldLeaveTheDatabaseOnline()
    {
        // Arrange: no checkpoint is due (no size trigger, an hour's time backstop) while the journal
        // grows past a cap of four times the pace size, with no fault.
        var strategy = new FaultInjectingJournalStorageStrategy(durable: true);
        var options = Options(strategy);
        options.CheckpointJournalSize = 0;
        options.JournalSizeLimit = 4 * paceJournalSize;
        await using var engine = GraphDatabaseEngine.Create("graph-engine", options);
        var database = await engine.CreateDatabaseAsync(failingDatabaseName);
        var worker = WorkerOf(engine, DatabaseEngineWorkerKind.Checkpoint);
        var faults = strategy.Faults(failingDatabaseName);
        for (int id = 0; database.DataStorage.JournalLength < options.JournalSizeLimit; id += 10)
        {
            await InsertAsync(database, id, 10);
        }

        long grown = database.DataStorage.JournalLength;

        // Act: the size trigger is armed and the first checkpoint fails; the fault clears before
        // the worker retries the database a backoff later.
        faults.FailPageWrites = true;
        database.DataStorage.CheckpointJournalSize = paceJournalSize;
        bool failed = await Eventually(() => worker.FailureCount >= 1);
        faults.FailPageWrites = false;
        bool truncated = await Eventually(() => worker.Fault is null && database.DataStorage.JournalLength < grown);

        // Assert
        grown.ShouldBeGreaterThanOrEqualTo(options.JournalSizeLimit);
        failed.ShouldBeTrue();
        truncated.ShouldBeTrue();
        worker.FailureCount.ShouldBe(1);
        database.IsOffline.ShouldBeFalse();
        engine.OfflineDatabases.ShouldBeEmpty();
        engine.GetOfflineError(failingDatabaseName).ShouldBeNull();
        engine.State.ShouldBe(EngineState.Running);
    }

    /// <summary>
    /// A database opened from a copied file set carries the original's name in its file header
    /// (a storage reads its name from there when it opens). The page write-back worker reports and
    /// escalates under the database's name in the engine, so the copy's persistent page write
    /// failures take only the copy offline, with its own cause (owner decision 25 review). Reported
    /// under the storage's name, they took the original offline and left the copy failing.
    /// </summary>
    [Fact(DisplayName = "Cohesion Test [Database.Graph] - Workers: a database opened from a copied file set goes offline for its own write-back failures, never the original")]
    public async Task PageWriteBackWorker_CopiedFileSet_ShouldTakeOnlyTheCopyOffline()
    {
        // Arrange: the page writer runs every 50 ms; the engine gives up after three failed
        // passes (a window of one tick).
        const string Copy = "copy";
        var strategy = new FaultInjectingJournalStorageStrategy(durable: true);
        var options = Options(strategy, writeBack: TimeSpan.FromMilliseconds(50));
        options.WorkerFailureWindow = TimeSpan.FromTicks(1);
        options.WorkerFailureMinimumPasses = 3;
        await using var engine = GraphDatabaseEngine.Create("graph-engine", options);
        var original = await engine.CreateDatabaseAsync(failingDatabaseName);
        await InsertAsync(original, 0, 10);
        await original.DisposeAsync();
        strategy.CopyDatabase(failingDatabaseName, Copy);
        original = await engine.OpenDatabaseAsync(failingDatabaseName);
        var copy = await engine.OpenDatabaseAsync(Copy);
        var worker = WorkerOf(engine, DatabaseEngineWorkerKind.PageWriteBack);

        // Act: every page write of the copy fails while it has dirty pages.
        strategy.Faults(Copy).FailPageWrites = true;
        await InsertAsync(copy, 100, 20);
        bool offline = await Eventually(() => engine.OfflineDatabases.Count > 0 && worker.Fault is null);
        var error = engine.GetOfflineError(Copy);
        strategy.Faults(Copy).FailPageWrites = false;

        // Assert
        ((string)copy.DataStorage.Name).ShouldBe(failingDatabaseName);
        offline.ShouldBeTrue();
        engine.OfflineDatabases.ShouldBe([(DatabaseName)Copy]);
        error.ShouldNotBeNull().Cause.ShouldBe(StorageOfflineCause.PageWriteBackFailures);
        error.Message.ShouldContain($"database '{Copy}'");
        original.IsOffline.ShouldBeFalse();
        engine.GetOfflineError(failingDatabaseName).ShouldBeNull();
        (await CountAsync(original)).ShouldBe(10);
    }

    /// <summary>
    /// A transient checkpoint failure leaves the database online (owner decisions 25 and 42): a
    /// streak of the minimum of failed passes ends when the fault clears and a checkpoint finishes,
    /// 60 s into the window on the engine's clock; a second streak starts the window again, so the
    /// database is never taken offline although its two streaks span 120 s of the clock.
    /// </summary>
    [Fact(DisplayName = "Cohesion Test [Database.Graph] - Workers: a transient checkpoint failure under the window leaves the database online, and the window starts again")]
    public async Task CheckpointWorker_TransientFailureUnderTheWindow_ShouldLeaveTheDatabaseOnline()
    {
        // Arrange: the checkpointer looks every 100 ms; the default window of 100 s and minimum of
        // three failed passes, on a clock the test moves.
        var clock = new ManualTimeProvider();
        var strategy = new FaultInjectingJournalStorageStrategy(durable: true);
        var options = Options(strategy, checkpoint: TimeSpan.FromMilliseconds(100));
        options.WorkerFailureWindow = DatabaseEngine.DefaultWorkerFailureWindow;
        options.WorkerFailureMinimumPasses = DatabaseEngine.DefaultWorkerFailureMinimumPasses;
        options.TimeProvider = clock;
        await using var engine = GraphDatabaseEngine.Create("graph-engine", options);
        var failing = await engine.CreateDatabaseAsync(failingDatabaseName);
        var worker = WorkerOf(engine, DatabaseEngineWorkerKind.Checkpoint);
        var faults = strategy.Faults(failingDatabaseName);

        // Act: two transient faults, each failing the minimum of passes over 60 s of the clock.
        var rounds = new List<(bool Failed, bool Recovered)>();
        for (int round = 0; round < 2; round++)
        {
            faults.FailPageWrites = true;
            long before = worker.FailureCount;
            await InsertAsync(failing, round * 100, 20);
            bool failed = await Eventually(() => worker.FailureCount > before);
            clock.Advance(TimeSpan.FromSeconds(60));
            failed &= await Eventually(() => worker.ConsecutiveFailures >= DatabaseEngine.DefaultWorkerFailureMinimumPasses);
            faults.FailPageWrites = false;
            long journal = failing.DataStorage.JournalLength;
            bool recovered = await Eventually(() => worker.Fault is null && failing.DataStorage.JournalLength < journal);
            rounds.Add((failed, recovered));
        }

        // Assert
        rounds.ShouldAllBe(round => round.Failed && round.Recovered);
        worker.FailureCount.ShouldBeGreaterThanOrEqualTo(2 * DatabaseEngine.DefaultWorkerFailureMinimumPasses);
        clock.Elapsed.ShouldBe(TimeSpan.FromSeconds(120));
        failing.IsOffline.ShouldBeFalse();
        engine.OfflineDatabases.ShouldBeEmpty();
        engine.GetOfflineError(failingDatabaseName).ShouldBeNull();
        engine.State.ShouldBe(EngineState.Running);
        (await CountAsync(failing)).ShouldBe(40);
    }

    /// <summary>
    /// A full version-purge pass that fails on one database is run again for that database alone a
    /// backoff after the failure, not at the next maintenance interval (owner decision 46), and the
    /// database's failure ends with the first retry that completes once the fault clears. The full
    /// pass and its retries are scheduled on the engine's clock, which the test moves, and the test
    /// runs the passes; the maintenance interval is an hour, so only the retry runs the full pass
    /// again. Before the decision a pass between full passes only retried deferred undo, so a failing
    /// full pass held its database's failure, and Blob's refusal of it, for a whole interval.
    /// </summary>
    [Fact(DisplayName = "Cohesion Test [Database.Graph] - Workers: a failed full version-purge pass is retried for its database alone after the backoff, and its failure ends once a retry completes")]
    public async Task VersionPurgeWorker_FullPassFails_ShouldRetryItAfterTheBackoffAndRecover()
    {
        // Arrange: a full pass due once an hour on a clock the test moves; a hook fails one
        // database's full pass until the test clears it.
        var clock = new ManualTimeProvider();
        var options = Options(new FaultInjectingJournalStorageStrategy());
        options.TimeProvider = clock;
        await using var engine = GraphDatabaseEngine.Create("graph-engine", options);
        await engine.CreateDatabaseAsync(failingDatabaseName);
        await engine.CreateDatabaseAsync(healthyDatabaseName);
        var worker = (GraphVersionPurgeWorker)WorkerOf(engine, DatabaseEngineWorkerKind.VersionPurge);
        var fullPasses = new ConcurrentDictionary<string, int>(StringComparer.Ordinal);
        bool fail = true;
        worker.BeforeFullPass = database =>
        {
            fullPasses.AddOrUpdate(database.Name, 1, static (_, count) => count + 1);
            if (Volatile.Read(ref fail) && database.Name == failingDatabaseName)
            {
                throw new InvalidOperationException("Injected version purge failure");
            }
        };

        // Act: the first full pass fails on one database.
        clock.Advance(options.MaintenanceInterval);
        CheckpointPace.RunSettled(worker, CancellationToken.None);
        var afterTheFailure = (Failing: fullPasses.GetValueOrDefault(failingDatabaseName), Healthy: fullPasses.GetValueOrDefault(healthyDatabaseName), Refused: engine.HasFailingWorker(failingDatabaseName));

        // A pass before the backoff has passed does not run it again, and keeps the failure.
        CheckpointPace.RunSettled(worker, CancellationToken.None);
        var beforeTheBackoff = (Failing: fullPasses.GetValueOrDefault(failingDatabaseName), Refused: engine.HasFailingWorker(failingDatabaseName));

        // The backoff passes: the full pass runs again for the failing database alone, and fails.
        clock.Advance(DatabaseEngineWorker.FailureBackoff);
        CheckpointPace.RunSettled(worker, CancellationToken.None);
        var afterTheRetry = (Failing: fullPasses.GetValueOrDefault(failingDatabaseName), Healthy: fullPasses.GetValueOrDefault(healthyDatabaseName), Failures: worker.ConsecutiveFailures);

        // The fault clears; the next retry, a backoff later, completes.
        Volatile.Write(ref fail, false);
        clock.Advance(DatabaseEngineWorker.FailureBackoff);
        CheckpointPace.RunSettled(worker, CancellationToken.None);

        // Assert
        afterTheFailure.ShouldBe((1, 1, true));
        beforeTheBackoff.ShouldBe((1, true));
        afterTheRetry.ShouldBe((2, 1, 2));
        fullPasses.GetValueOrDefault(failingDatabaseName).ShouldBe(3);
        fullPasses.GetValueOrDefault(healthyDatabaseName).ShouldBe(1);
        worker.Fault.ShouldBeNull();
        worker.ConsecutiveFailures.ShouldBe(0);
        engine.HasFailingWorker(failingDatabaseName).ShouldBeFalse();
        engine.State.ShouldBe(EngineState.Running);
        engine.OfflineDatabases.ShouldBeEmpty();
    }

    /// <summary>
    /// A full version-purge pass that keeps failing takes only its database offline at about the
    /// engine's worker failure window (owner decisions 42 and 46): retried a backoff after each
    /// failure, its third failed pass in a row comes 101 s after its first on the engine's clock,
    /// which the test moves, past the default window of 100 s. Retried only at the next maintenance
    /// interval, an hour here, it took two more intervals.
    /// </summary>
    [Fact(DisplayName = "Cohesion Test [Database.Graph] - Workers: a full version-purge pass that keeps failing takes only its database offline at about the window, not intervals later")]
    public async Task VersionPurgeWorker_FullPassKeepsFailing_ShouldTakeOnlyItsDatabaseOfflineAtAboutTheWindow()
    {
        // Arrange: the default window of 100 s and minimum of three failed passes, and a full pass
        // due once an hour, on a clock the test moves; a hook fails one database's full pass.
        var clock = new ManualTimeProvider();
        var options = Options(new FaultInjectingJournalStorageStrategy());
        options.WorkerFailureWindow = DatabaseEngine.DefaultWorkerFailureWindow;
        options.WorkerFailureMinimumPasses = DatabaseEngine.DefaultWorkerFailureMinimumPasses;
        options.TimeProvider = clock;
        await using var engine = GraphDatabaseEngine.Create("graph-engine", options);
        var failing = await engine.CreateDatabaseAsync(failingDatabaseName);
        var healthy = await engine.CreateDatabaseAsync(healthyDatabaseName);
        var worker = (GraphVersionPurgeWorker)WorkerOf(engine, DatabaseEngineWorkerKind.VersionPurge);
        worker.BeforeFullPass = database =>
        {
            if (database.Name == failingDatabaseName)
            {
                throw new InvalidOperationException("Injected version purge failure");
            }
        };

        // Act: the full pass fails, its retry a backoff later fails, and the retry once the window
        // has passed is the third failed pass in a row.
        clock.Advance(options.MaintenanceInterval);
        worker.RunIteration(CancellationToken.None);
        clock.Advance(DatabaseEngineWorker.FailureBackoff);
        worker.RunIteration(CancellationToken.None);
        bool onlineInsideTheWindow = !failing.IsOffline && engine.OfflineDatabases.Count == 0;
        clock.Advance(DatabaseEngine.DefaultWorkerFailureWindow);
        worker.RunIteration(CancellationToken.None);
        bool offline = await Eventually(() => engine.OfflineDatabases.Contains((DatabaseName)failingDatabaseName));
        var refusal = await Should.ThrowAsync<DatabaseOfflineException>(async () => await failing.CreateSessionAsync());

        // Assert
        onlineInsideTheWindow.ShouldBeTrue();
        offline.ShouldBeTrue();
        refusal.Code.ShouldBe("CG06012");
        refusal.Message.ShouldContain("its version purge kept failing");
        var cause = StorageOfflineException.Find(refusal).ShouldNotBeNull();
        cause.Cause.ShouldBe(StorageOfflineCause.VersionPurgeFailures);
        cause.Message.ShouldContain($"'{worker.Name}'");
        cause.Message.ShouldContain("on 3 passes in a row over 101 s, at least the engine's window of 100 s", Case.Sensitive);
        Mentions(refusal, "Injected version purge failure").ShouldBeTrue(refusal.ToString());
        engine.OfflineDatabases.ShouldBe([(DatabaseName)failingDatabaseName]);
        healthy.IsOffline.ShouldBeFalse();
        await using var healthySession = await healthy.CreateSessionAsync();
    }

    [Fact(DisplayName = "Cohesion Test [Database.Graph] - Workers: the worker failure window, its minimum of passes and the journal cap have their defaults, are validated, and reach the engine through the builder")]
    public async Task Options_WorkerLimits_ShouldDefaultValidateAndReachTheEngine()
    {
        // Arrange
        var defaults = new GraphDatabaseEngineOptions();
        var builder = GraphDatabaseEngine.CreateBuilder("graph-engine");
        builder.Options.WorkerFailureWindow = TimeSpan.FromSeconds(30);
        builder.Options.WorkerFailureMinimumPasses = 4;
        builder.Options.JournalSizeLimit = 512L * 1024 * 1024;

        // Act
        await using var engine = GraphDatabaseEngine.Create("graph-engine", new());
        await using var timeOnly = GraphDatabaseEngine.Create("graph-engine", new() { CheckpointJournalSize = 0 });
        await using var built = builder.Build();

        // Assert: a hundred seconds across at least three failed passes (owner decision 42), and
        // four times the checkpoint size (1 GiB at its default, or with the size trigger off); a
        // window that is not positive or past the maximum, a minimum below one pass, and a journal
        // limit set below the checkpoint size are refused.
        defaults.WorkerFailureWindow.ShouldBe(DatabaseEngine.DefaultWorkerFailureWindow);
        defaults.WorkerFailureMinimumPasses.ShouldBe(DatabaseEngine.DefaultWorkerFailureMinimumPasses);
        defaults.JournalSizeLimit.ShouldBe(0);
        engine.WorkerFailureWindow.ShouldBe(TimeSpan.FromSeconds(100));
        engine.WorkerFailureMinimumPasses.ShouldBe(3);
        engine.JournalSizeLimit.ShouldBe(4 * defaults.CheckpointJournalSize);
        timeOnly.JournalSizeLimit.ShouldBe(1024L * 1024 * 1024);
        built.WorkerFailureWindow.ShouldBe(TimeSpan.FromSeconds(30));
        built.WorkerFailureMinimumPasses.ShouldBe(4);
        built.JournalSizeLimit.ShouldBe(512L * 1024 * 1024);
        Should.Throw<ArgumentOutOfRangeException>(() => GraphDatabaseEngine.Create("graph-engine", new() { WorkerFailureWindow = TimeSpan.Zero }))
            .ParamName.ShouldBe(nameof(GraphDatabaseEngineOptions.WorkerFailureWindow));
        Should.Throw<ArgumentOutOfRangeException>(() => GraphDatabaseEngine.Create("graph-engine", new() { WorkerFailureWindow = DatabaseEngine.MaximumWorkerFailureWindow + TimeSpan.FromTicks(1) }))
            .ParamName.ShouldBe(nameof(GraphDatabaseEngineOptions.WorkerFailureWindow));
        Should.Throw<ArgumentOutOfRangeException>(() => GraphDatabaseEngine.Create("graph-engine", new() { WorkerFailureMinimumPasses = 0 }))
            .ParamName.ShouldBe(nameof(GraphDatabaseEngineOptions.WorkerFailureMinimumPasses));
        Should.Throw<ArgumentOutOfRangeException>(() => GraphDatabaseEngine.Create("graph-engine", new() { JournalSizeLimit = -1 }))
            .ParamName.ShouldBe(nameof(GraphDatabaseEngineOptions.JournalSizeLimit));
        Should.Throw<ArgumentOutOfRangeException>(() => GraphDatabaseEngine.Create("graph-engine", new() { CheckpointJournalSize = 1024 * 1024, JournalSizeLimit = 1024 }))
            .ParamName.ShouldBe(nameof(GraphDatabaseEngineOptions.JournalSizeLimit));
    }

    [Theory(DisplayName = "Cohesion Test [Database.Graph] - Workers: one stalled database leaves healthy lane passes at full pace")]
    [InlineData(DatabaseEngineWorkerKind.PageWriteBack, 20)]
    [InlineData(DatabaseEngineWorkerKind.WriteAheadFlush, 20)]
    [InlineData(DatabaseEngineWorkerKind.VersionPurge, 20)]
    [InlineData(DatabaseEngineWorkerKind.PageWriteBack, 150)]
    [InlineData(DatabaseEngineWorkerKind.VersionPurge, 150)]
    public async Task Run_OneDatabaseStalls_ShouldKeepOtherDatabasesAtFullPace(DatabaseEngineWorkerKind kind, int cadenceMilliseconds)
    {
        // Arrange: only the chosen worker has a short cadence. Checkpoints cannot supply progress.
        var strategy = new FaultInjectingJournalStorageStrategy(durable: true);
        var options = Options(strategy);
        options.PageWriteBackInterval = kind == DatabaseEngineWorkerKind.PageWriteBack ? TimeSpan.FromMilliseconds(cadenceMilliseconds) : TimeSpan.FromHours(1);
        options.MaintenanceInterval = TimeSpan.FromMilliseconds(cadenceMilliseconds);
        options.Durability = kind == DatabaseEngineWorkerKind.WriteAheadFlush ? StorageCommitDurability.Grouped : StorageCommitDurability.Synchronous;
        options.GroupCommitWindow = TimeSpan.FromMilliseconds(cadenceMilliseconds);
        await using var engine = GraphDatabaseEngine.Create("isolated-lanes", options);
        var failing = await engine.CreateDatabaseAsync(failingDatabaseName);
        var healthy = await engine.CreateDatabaseAsync(healthyDatabaseName);
        var faults = strategy.Faults(failingDatabaseName);
        Task blockedWrite = Task.CompletedTask;
        Task? disposal = null;
        async Task InspectDisposalAsync()
        {
            // First prove resumption, then re-arm the same gate to retain the disposal/join proof.
            await blockedWrite.WaitAsync(_timeout);
            if (kind == DatabaseEngineWorkerKind.PageWriteBack)
            {
                await CheckpointPace.HoldPageWritesAsync((GraphPageWriteBackWorker)WorkerOf(engine, kind),
                    failing, faults, () => InsertAsync(failing, -1, 1));
            }
            else if (kind == DatabaseEngineWorkerKind.WriteAheadFlush)
            {
                faults.StallJournalFlushes();
                blockedWrite = DedicatedTestTask.Run(() => InsertAsync(failing, -1, 1));
            }
            else { faults.StallPurges(); }
            (await Eventually(() => faults.StalledPageWrites + faults.StalledJournalFlushes + faults.StalledPurges == 1)).ShouldBeTrue();
            disposal = DedicatedTestTask.Run(async () => await engine.DisposeAsync());
            (await Eventually(() => engine.State == EngineState.Disposed)).ShouldBeTrue();
            disposal.IsCompleted.ShouldBeFalse("engine disposal must join the held lane before closing storage");
        }
        long written = strategy.Faults(healthyDatabaseName).PageWrites;
        try
        {
            // Act: page writes and journal fsyncs stall inside DeviceFaults. Purge/index use
            // the dispatch hook, modeling an unbounded apply/storage latch without a new API.
            if (kind == DatabaseEngineWorkerKind.PageWriteBack)
            {
                var worker = (GraphPageWriteBackWorker)WorkerOf(engine, kind);
                await CheckpointPace.HoldPageWritesAsync(worker, failing, faults, () => InsertAsync(failing, 0, 1));
                await CheckpointPace.VerifyIsolationAsync(worker, failingDatabaseName, healthyDatabaseName, faults,
                    () => faults.StalledPageWrites, id => InsertAsync(healthy, id, 1), InspectDisposalAsync);
                strategy.Faults(healthyDatabaseName).PageWrites.ShouldBeGreaterThan(written);
            }
            else if (kind == DatabaseEngineWorkerKind.WriteAheadFlush)
            {
                faults.StallJournalFlushes();
                blockedWrite = DedicatedTestTask.Run(() => InsertAsync(failing, 0, 1));
                var worker = (GraphWriteAheadFlushWorker)WorkerOf(engine, kind);
                await CheckpointPace.VerifyIsolationAsync(worker, failingDatabaseName, healthyDatabaseName, faults,
                    () => faults.StalledJournalFlushes, id => InsertAsync(healthy, id, 1), InspectDisposalAsync);
            }
            else if (kind == DatabaseEngineWorkerKind.VersionPurge)
            {
                faults.StallPurges();
                var worker = (GraphVersionPurgeWorker)WorkerOf(engine, kind);
                worker.BeforeDatabase = database => { if (ReferenceEquals(database, failing)) { faults.BeforePurge(); } };
                await CheckpointPace.VerifyIsolationAsync(worker, failingDatabaseName, healthyDatabaseName, faults,
                    () => faults.StalledPurges, id => InsertAsync(healthy, id, 1), InspectDisposalAsync);
            }
        }
        finally
        {
            faults.Clear();
            await blockedWrite.WaitAsync(_timeout);
            if (disposal is not null) { await disposal.WaitAsync(_timeout); }
        }
        // Assert: released work drains, and no database was failed or taken offline by a stall.
        (await Eventually(() => faults.StalledPageWrites == 0 && faults.StalledJournalFlushes == 0 && faults.StalledPurges == 0)).ShouldBeTrue();
        engine.OfflineDatabases.ShouldBeEmpty();
    }

    [Fact(DisplayName = "Cohesion Test [Database.Graph] - Isolation samples: a slow healthy producer and extra WAL wakes still require positive work")]
    public async Task Run_OneDatabaseStallsWithSlowHealthyProducer_ShouldWaitForRealWorkSamples()
    {
        // Arrange: unrelated registrations can keep a WAL pump visiting an idle database
        // while its producer is scheduled slowly under load. No-op visits are not I/O samples.
        var strategy = new FaultInjectingJournalStorageStrategy(durable: true);
        var options = Options(strategy);
        options.Durability = StorageCommitDurability.Grouped;
        options.GroupCommitWindow = TimeSpan.FromSeconds(1);
        await using var engine = GraphDatabaseEngine.Create("slow-producer", options);
        var failing = await engine.CreateDatabaseAsync(failingDatabaseName);
        var healthy = await engine.CreateDatabaseAsync(healthyDatabaseName);
        var faults = strategy.Faults(failingDatabaseName);
        var worker = (GraphWriteAheadFlushWorker)WorkerOf(engine, DatabaseEngineWorkerKind.WriteAheadFlush);
        using var stop = new CancellationTokenSource();
        var wakes = DedicatedTestTask.Run(async () =>
        {
            while (!stop.IsCancellationRequested)
            {
                healthy.DataStorage.OnCommitPending?.Invoke();
                await Task.Delay(5);
            }
        });
        faults.StallJournalFlushes();
        var blocked = DedicatedTestTask.Run(() => InsertAsync(failing, 0, 1));
        try
        {
            // Act/Assert: the helper's continuation must wait for its positive-work counter,
            // retaining the hang guard and positive-work assertions.
            await CheckpointPace.VerifyIsolationAsync(worker, failingDatabaseName, healthyDatabaseName, faults,
                () => faults.StalledJournalFlushes, async id =>
                {
                    await Task.Delay(100);
                    await InsertAsync(healthy, id, 1);
                });
        }
        finally
        {
            stop.Cancel(); faults.Clear();
            await Task.WhenAll(wakes, blocked).WaitAsync(_timeout);
        }
        engine.OfflineDatabases.ShouldBeEmpty();
    }

    // Journal size used to exercise checkpoint lanes and failure retries.
    private const long paceJournalSize = 64 * 1024;

    // The worker failure minimum of passes and the journal cap are out of reach unless a test sets
    // them (owner decisions 25 and 42): the retry tests keep a database failing for seconds on purpose, and the pace
    // pace test observes its failures, retries and backoff skips.
    private static GraphDatabaseEngineOptions Options(FaultInjectingJournalStorageStrategy strategy, TimeSpan? checkpoint = null, TimeSpan? writeBack = null) => new()
    {
        StorageStrategy = strategy,
        CheckpointInterval = checkpoint ?? TimeSpan.FromHours(1),
        PageWriteBackInterval = writeBack ?? TimeSpan.FromHours(1),
        MaintenanceInterval = TimeSpan.FromHours(1),
        WorkerFailureMinimumPasses = int.MaxValue,
        JournalSizeLimit = long.MaxValue,
    };

    private static DatabaseEngineWorker WorkerOf(GraphDatabaseEngine engine, DatabaseEngineWorkerKind kind)
        => engine.Workers.OfType<DatabaseEngineWorker>().Single(worker => worker.Kind == kind);

    private static async Task InsertAsync(GraphDatabase database, int first, int count)
    {
        await using var session = await database.CreateSessionAsync();
        for (int id = first; id < first + count; id++)
        {
            await session.ExecuteAsync($"INSERT (:Item {{name: 'k{id}', payload: '{new string('x', 150)}'}})");
        }
    }

    private static async Task WriteGroupedInsertsAsync(GraphDatabase database, int count)
    {
        await using var session = await database.CreateSessionAsync();
        for (int id = 0; id < count; id++)
        {
            await session.ExecuteAsync($"INSERT (:Item {{name: 'grouped-{id}'}})");
        }
    }

    private static async Task<int> CountAsync(GraphDatabase database)
    {
        await using var session = await database.CreateSessionAsync();
        var result = await session.ExecuteAsync("MATCH (n:Item) RETURN n.name");
        var set = result.ShouldBeAssignableTo<DatabaseResultSet>().ShouldNotBeNull();
        int count = 0;
        await using (set)
        {
            await foreach (var _ in set.GetRowsAsync())
            {
                count++;
            }
        }

        return count;
    }

    private static async Task WriteFrameAsync(ProtocolChannel channel, ProtocolMessageType type, byte[] payload, CancellationToken token)
    {
        await channel.Writer.WriteFrameAsync(new(type, payload), token);
        await channel.Writer.FlushAsync(token);
    }

    private static async Task<ProtocolFrame> ReadFrameAsync(ProtocolChannel channel, CancellationToken token)
        => await channel.Reader.ReadFrameAsync(token) ?? throw new ProtocolException("Unexpected end of graph exchange.");

    private static bool Mentions(Exception? error, string text)
    {
        for (int depth = 0; error is not null && depth < 16; depth++, error = error.InnerException)
        {
            if (error.Message.Contains(text, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    // Polls a condition the engine's workers make true, for at most the test timeout.
    private static async Task<bool> Eventually(Func<bool> condition)
    {
        var watch = Stopwatch.StartNew();
        while (!condition())
        {
            if (watch.Elapsed > _timeout)
            {
                return false;
            }

            await Task.Delay(10);
        }

        return true;
    }

    /// <summary>
    /// A registered worker whose first pass throws; later passes run until cancelled, so the
    /// failure stays recorded (no pass ran to its end) while the test reads the engine's state.
    /// </summary>
    private sealed class EscapingWorker : DatabaseEngineWorker
    {
        private int _runs;
        private int _stopped;

        public EscapingWorker()
            : base("escaping", DatabaseEngineWorkerKind.IndexMaintenance, TimeSpan.FromMilliseconds(10))
        {
        }

        public int Runs => Volatile.Read(ref _runs);

        public bool Stopped => Volatile.Read(ref _stopped) != 0;

        protected override void RunIterationCore(CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref _runs) == 1)
            {
                throw new InvalidOperationException("The worker's pass failed.");
            }

            cancellationToken.WaitHandle.WaitOne();
            Volatile.Write(ref _stopped, 1);
            cancellationToken.ThrowIfCancellationRequested();
        }
    }
}
