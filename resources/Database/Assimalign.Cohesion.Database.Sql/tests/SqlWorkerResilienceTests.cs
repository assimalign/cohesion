using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.Sql.Internal;
using Assimalign.Cohesion.Database.Sql.Tests.TestObjects;
using Assimalign.Cohesion.Database.Storage;
using Assimalign.Cohesion.Database.Storage.Tests.TestObjects;
using Assimalign.Cohesion.Database.Tests;

namespace Assimalign.Cohesion.Database.Sql.Tests;

/// <summary>
/// The SQL engine's background workers under device faults that fire on their own threads
/// (#1268): a checkpoint or page write-back whose page writes fail is reported while the fault
/// lasts, retried after the worker's backoff, and recovers when the fault clears, while the
/// engine's other database keeps its work; a failed group-commit drain (#1252) or fsync and a
/// failed header slot write take only their database offline, and every later operation on it is
/// refused with COHSQLT004 while nothing more is written to it. Before #1268 the first page write
/// failure ended the checkpoint worker for good, and a failed header write left the database
/// accepting commits whose journal no checkpoint could truncate.
/// </summary>
[CollectionDefinition(nameof(SqlWorkerResilienceTests), DisableParallelization = true)]
[Collection(nameof(SqlWorkerResilienceTests))]
public sealed class SqlWorkerResilienceTests
{
    private const string Failing = "sql-worker-resilience-failing";
    private const string Healthy = "sql-worker-resilience-healthy";
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    [Theory(DisplayName = "Cohesion Test [Database.Sql] - Workers: failed provisional flushes reach construction without faulting the worker")]
    [InlineData(DeviceFault.JournalWrite)]
    [InlineData(DeviceFault.JournalFlush)]
    public async Task WriteAheadFlushWorker_ProvisionalFlushFails_ShouldKeepServingAfterFailedConstruction(DeviceFault fault)
    {
        // Arrange
        var strategy = new FaultInjectingJournalSqlStorageStrategy(durable: true);
        var options = Options(strategy);
        options.Durability = StorageCommitDurability.Grouped;
        options.GroupCommitWindow = TimeSpan.FromSeconds(10);
        using var commits = new GroupCommitRecorder();
        await using var engine = SqlDatabaseEngine.Create("provisional-engine", options);
        var worker = WorkerOf(engine, DatabaseEngineWorkerKind.WriteAheadFlush);
        var faults = strategy.Faults(Failing, catalog: true);
        faults.SwitchOn(fault);

        // Act: the constructor receives the failed flush and disposes the provisional file sets.
        try
        {
            var failure = await Record.ExceptionAsync(async () => await engine.CreateDatabaseAsync(Failing));
            StorageOfflineException.Find(failure.ShouldNotBeNull()).ShouldNotBeNull();
        }
        finally
        {
            faults.Clear();
        }
        await engine.CreateDatabaseAsync(Healthy);

        // Assert: the worker survives the stale snapshot and serves the next construction.
        (await Eventually(() => commits.FlushCount(Healthy) > 0)).ShouldBeTrue();
        commits.MissedCount(Healthy).ShouldBe(0);
        worker.FailureCount.ShouldBe(0);
        worker.Fault.ShouldBeNull();
        engine.GetStorageSnapshot().Length.ShouldBe(2);
    }

    [Fact(DisplayName = "Cohesion Test [Database.Sql] - Workers: grouped create and reopen commits are flushed before publication")]
    public async Task WriteAheadFlushWorker_ProvisionalDatabases_ShouldFlushCreateAndOpenBeforeSelfHelp()
    {
        // Arrange: construction holds the engine lock and cannot publish until its commits finish.
        var strategy = new FaultInjectingJournalSqlStorageStrategy(durable: true);
        var options = Options(strategy);
        options.Durability = StorageCommitDurability.Grouped;
        options.GroupCommitWindow = TimeSpan.FromSeconds(10);
        using var commits = new GroupCommitRecorder();
        await using var engine = SqlDatabaseEngine.Create("provisional-engine", options);
        var worker = WorkerOf(engine, DatabaseEngineWorkerKind.WriteAheadFlush);
        var duration = Stopwatch.StartNew();
        int creates = 0;
        int opens = 0;

        // Act: repeat until both publication races have run, for at least a quarter second.
        do
        {
            string name = "sql-worker-resilience-provisional-" + creates;
            var database = await engine.CreateDatabaseAsync(name);
            (await Eventually(() => commits.FlushCount(name) > 0)).ShouldBeTrue();
            creates++;

            // Leave an unproven logical writer, so reopen must commit a recovery scrub.
            // A clean reopen may need no grouped commit and would prove nothing about the worker.
            var storage = database.DataStorage;
            storage.WriteAheadJournal.AppendBegin(storage.ReserveTransactionSequence());
            storage.WriteAheadJournal.Flush(forceDurable: true);
            storage.TakeOffline(StorageOfflineCause.RecoveryInterrupted,
                "Simulated power loss before transaction end.", new IOException("Simulated unfinished writer."));
            await database.DisposeAsync();
            int beforeOpen = commits.FlushCount(name);
            var reopened = await engine.OpenDatabaseAsync(name);
            (await Eventually(() => commits.FlushCount(name) > beforeOpen)).ShouldBeTrue();
            commits.MissedCount(name).ShouldBe(0);
            opens++;
            await reopened.DisposeAsync();
        }
        while ((duration.Elapsed < TimeSpan.FromMilliseconds(250) || creates < 3 || opens < 3)
            && duration.Elapsed < Timeout);

        // Assert
        creates.ShouldBeGreaterThanOrEqualTo(3);
        opens.ShouldBe(creates);
        worker.FailureCount.ShouldBe(0);
        worker.Fault.ShouldBeNull();
        engine.GetStorageSnapshot().ShouldBeEmpty();
    }

    [Fact(DisplayName = "Cohesion Test [SqlEngine] - Workers: a checkpoint whose page writes fail is retried after a backoff, and recovers when the fault clears")]
    public async Task CheckpointWorker_PageWritesFail_ShouldRetryAndRecoverWhileOtherDatabasesCheckpoint()
    {
        // Arrange: the checkpointer looks every 100 ms; nothing else writes pages back.
        var strategy = new FaultInjectingJournalSqlStorageStrategy(durable: true);
        await using var engine = SqlDatabaseEngine.Create("sql-engine", Options(strategy, checkpoint: TimeSpan.FromMilliseconds(100)));
        var failing = await CreateAsync(engine, Failing);
        var healthy = await CreateAsync(engine, Healthy);
        var worker = WorkerOf(engine, DatabaseEngineWorkerKind.Checkpoint);
        var workers = engine.Workers.ToArray();
        var faults = strategy.Faults(Failing);

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

        // Assert: reported while it lasted, retried no faster than the backoff allows, and the
        // worker, not a replacement, completed the checkpoint once the fault cleared.
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
    /// Measured waits and due-state evidence distinguish a database backoff from a pump or lane
    /// slowdown; completed worker visits prove progress independently of statement checkpoints (#1284).
    /// </summary>
    [Fact(DisplayName = "Cohesion Test [Database.Sql] - Workers: a failing database leaves every healthy pass unthrottled")]
    public async Task CheckpointWorker_OneDatabaseKeepsFailing_ShouldKeepTheOthersAtFullPace()
    {
        // Arrange: observe the actual engine pump and its database lanes, without changing waits.
        var strategy = new FaultInjectingJournalSqlStorageStrategy(durable: true);
        var options = Options(strategy, checkpoint: TimeSpan.FromMilliseconds(100));
        options.CheckpointJournalSize = PaceJournalSize;
        await using var engine = SqlDatabaseEngine.Create("pace-engine", options);
        var failing = await CreateAsync(engine, Failing);
        var healthy = await CreateAsync(engine, Healthy);
        var worker = (SqlCheckpointWorker)WorkerOf(engine, DatabaseEngineWorkerKind.Checkpoint);
        // Act / Assert: all five engines use the same per-visit progress proof.
        await CheckpointPace.VerifyAsync(worker, engine.CheckpointNeededSignal, failing.DataStorage, healthy.DataStorage,
            Failing, Healthy, strategy.Faults(Failing),
            id => InsertAsync(failing, id, 10), id => InsertAsync(healthy, id, 10), PaceJournalSize);
    }

    /// <summary>
    /// One database's checkpoint that hangs in a durable flush of its data file — an fsync its
    /// device does not answer — must not hold back the checkpoints of the engine's other databases.
    /// Before the checkpoint lanes, the worker visited the databases one by one on its own thread, so
    /// the hung fsync stopped every checkpoint of the engine until it returned: a device that took
    /// seconds to answer, or to fail, stalled every other database's journal truncation for as long.
    /// </summary>
    [Fact(DisplayName = "Cohesion Test [SqlEngine] - Workers: a database whose checkpoint fsync hangs does not hold back the other database's checkpoints")]
    public async Task CheckpointWorker_OneDatabaseFsyncHangs_ShouldKeepCheckpointingTheOthers()
    {
        // Arrange: checkpoints by journal size; nothing else writes pages back.
        var strategy = new FaultInjectingJournalSqlStorageStrategy(durable: true);
        var options = Options(strategy);
        options.CheckpointJournalSize = PaceJournalSize;
        await using var engine = SqlDatabaseEngine.Create("sql-engine", options);
        var stalled = await CreateAsync(engine, Failing);
        var healthy = await CreateAsync(engine, Healthy);
        var worker = WorkerOf(engine, DatabaseEngineWorkerKind.Checkpoint);
        var stalledFaults = strategy.Faults(Failing);
        var healthyFaults = strategy.Faults(Healthy);

        try
        {
            // The stalled database's journal grows past the size with its trigger off, so no
            // statement takes its checkpoint over: the worker runs it, and hangs in its data fsync.
            stalled.DataStorage.CheckpointJournalSize = 0;
            for (int id = 0; stalled.DataStorage.JournalLength < PaceJournalSize; id += 10)
            {
                await InsertAsync(stalled, id, 10);
            }

            stalledFaults.StallDataFlushes();
            stalled.DataStorage.CheckpointJournalSize = PaceJournalSize;
            bool hung = await Eventually(() => stalledFaults.StalledDataFlushes > 0);
            long stalledJournal = stalled.DataStorage.JournalLength;

            // Act: while the fsync hangs, the healthy database's journal passes the size again and again.
            long checkpoints = healthyFaults.HeaderWrites;
            var watch = Stopwatch.StartNew();
            for (int id = 0; healthyFaults.HeaderWrites - checkpoints < 3 && watch.Elapsed < StallWindow; id += 10)
            {
                await InsertAsync(healthy, id, 10);
            }

            long healthyCheckpoints = healthyFaults.HeaderWrites - checkpoints;
            bool stillHung = stalledFaults.StalledDataFlushes > 0;
            long stalledJournalDuringTheHang = stalled.DataStorage.JournalLength;

            stalledFaults.ReleaseDataFlushes();
            bool stalledCheckpointed = await Eventually(() => stalled.DataStorage.JournalLength < stalledJournal);

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

    [Fact(DisplayName = "Cohesion Test [SqlEngine] - Workers: a page write-back that fails is retried after a backoff, and the pages reach the file once the fault clears")]
    public async Task PageWriteBackWorker_PageWritesFail_ShouldRetryAndRecoverWhileOtherDatabasesAreWritten()
    {
        // Arrange: the page writer runs every 50 ms; no checkpoint writes pages.
        var strategy = new FaultInjectingJournalSqlStorageStrategy(durable: true);
        await using var engine = SqlDatabaseEngine.Create("sql-engine", Options(strategy, writeBack: TimeSpan.FromMilliseconds(50)));
        var failing = await CreateAsync(engine, Failing);
        var healthy = await CreateAsync(engine, Healthy);
        var worker = WorkerOf(engine, DatabaseEngineWorkerKind.PageWriteBack);
        var faults = strategy.Faults(Failing);
        var healthyFaults = strategy.Faults(Healthy);

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

        var atTheClear = strategy.Capture(Failing).Data;
        faults.FailPageWrites = false;
        bool recovered = await Eventually(() =>
            engine.State == EngineState.Running && !strategy.Capture(Failing).Data.AsSpan().SequenceEqual(atTheClear));

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
    [Theory(DisplayName = "Cohesion Test [SqlEngine] - Workers: a group-commit drain or fsync failure takes only its database offline, and the flush worker keeps serving the others")]
    [InlineData(DeviceFault.JournalFlush)]
    [InlineData(DeviceFault.JournalWrite)]
    public async Task WriteAheadFlushWorker_JournalFails_ShouldTakeOnlyItsDatabaseOfflineAndKeepFlushing(DeviceFault fault)
    {
        // Arrange: grouped commits wait up to two seconds for the flush worker before they flush
        // themselves, so a commit that returns sooner was flushed by the worker.
        var window = TimeSpan.FromSeconds(2);
        var strategy = new FaultInjectingJournalSqlStorageStrategy(durable: true);
        var options = Options(strategy);
        options.Durability = StorageCommitDurability.Grouped;
        options.GroupCommitWindow = window;
        await using var engine = SqlDatabaseEngine.Create("sql-engine", options);
        var failing = await CreateAsync(engine, Failing);
        var healthy = await CreateAsync(engine, Healthy);
        var worker = WorkerOf(engine, DatabaseEngineWorkerKind.WriteAheadFlush);
        var faults = strategy.Faults(Failing);
        await using var session = await failing.CreateSessionAsync();

        // Act: the failing database's journal drain, or its fsync, fails under the worker's group
        // flush.
        faults.SwitchOn(fault);
        var watch = Stopwatch.StartNew();
        var error = await Record.ExceptionAsync(async () => await session.ExecuteAsync("INSERT INTO t (id, payload) VALUES (1, 'lost')"));
        var failedCommit = watch.Elapsed;
        var refusal = await Should.ThrowAsync<DatabaseOfflineException>(async () => await failing.CreateSessionAsync());

        // The worker keeps flushing the other database's grouped commits.
        var latencies = await TimedInsertsAsync(healthy, 5);

        faults.Clear();
        var reopened = await engine.OpenDatabaseAsync(Failing);

        // Assert: the commit failed fast (the worker's flush failed and released it), only its
        // database went offline, and the worker reported no failure of its own. A failed drain
        // ends the flush before its fsync.
        bool drain = fault == DeviceFault.JournalWrite;
        StorageOfflineException.Find(error.ShouldNotBeNull()).ShouldNotBeNull();
        (failedCommit / window).ShouldBeLessThan(1.0);
        (drain ? faults.JournalWriteFailures : faults.JournalFlushFailures).ShouldBeGreaterThanOrEqualTo(1);
        (drain ? faults.JournalWriteFailureThread : faults.JournalFlushFailureThread).ShouldBe(worker.Name);
        (drain ? faults.JournalFlushFailures : faults.JournalWriteFailures).ShouldBe(0);
        refusal.Code.ShouldBe("COHSQLT004");
        refusal.Message.ShouldContain("a write or flush of the journal");
        StorageOfflineException.Find(refusal)!.Cause.ShouldBe(StorageOfflineCause.JournalFlush);
        StorageOfflineException.Find(refusal)!.Message.ShouldContain(drain ? "a write of the journal" : "a durable flush of the journal");
        (latencies.Max() / window).ShouldBeLessThan(1.0, $"grouped commit latencies {string.Join(", ", latencies)}");
        worker.Fault.ShouldBeNull();
        worker.FailureCount.ShouldBe(0);
        engine.State.ShouldBe(EngineState.Running);
        reopened.IsOffline.ShouldBeFalse();
        engine.OfflineDatabases.ShouldBeEmpty();
    }

    [Fact(DisplayName = "Cohesion Test [SqlEngine] - Workers: a failed header slot write takes the database offline: later operations are refused and its files stop growing")]
    public async Task CheckpointWorker_HeaderWriteFails_ShouldTakeTheDatabaseOfflineUntilReopened()
    {
        // Arrange
        var strategy = new FaultInjectingJournalSqlStorageStrategy(durable: true);
        await using var engine = SqlDatabaseEngine.Create("sql-engine", Options(strategy, checkpoint: TimeSpan.FromMilliseconds(100)));
        var failing = await CreateAsync(engine, Failing);
        var healthy = await CreateAsync(engine, Healthy);
        await InsertAsync(failing, 0, 10);
        var worker = WorkerOf(engine, DatabaseEngineWorkerKind.Checkpoint);
        var faults = strategy.Faults(Failing);
        await using var session = await failing.CreateSessionAsync();

        // Act: the next checkpoint's header slot write fails.
        faults.FailHeaderWrites = true;
        await session.ExecuteAsync("INSERT INTO t (id, payload) VALUES (10, 'before the fault')");
        bool offline = await Eventually(() => engine.OfflineDatabases.Contains((DatabaseName)Failing));
        long journalAtTheFault = failing.DataStorage.JournalLength;
        var dataAtTheFault = strategy.Capture(Failing);
        var catalogAtTheFault = strategy.Capture(Failing, catalog: true);

        var refusals = new List<DatabaseOfflineException>
        {
            await Should.ThrowAsync<DatabaseOfflineException>(async () => await failing.CreateSessionAsync()),
            await Should.ThrowAsync<DatabaseOfflineException>(async () => await session.ExecuteAsync("INSERT INTO t (id, payload) VALUES (11, 'refused')")),
            await Should.ThrowAsync<DatabaseOfflineException>(async () => await session.BeginTransactionAsync()),
        };

        // The checkpoint worker keeps visiting the engine: the healthy database is checkpointed.
        await InsertAsync(healthy, 0, 10);
        long healthyWritten = healthy.DataStorage.JournalLength;
        bool healthyCheckpointed = await Eventually(() => healthy.DataStorage.JournalLength < healthyWritten);
        var dataAfter = strategy.Capture(Failing);
        var catalogAfter = strategy.Capture(Failing, catalog: true);

        faults.Clear();
        var reopened = await engine.OpenDatabaseAsync(Failing);

        // Assert: offline with the coded refusal naming the header write, nothing written after
        // the failure, and no retry storm: the slot write was tried once.
        offline.ShouldBeTrue();
        refusals.ShouldAllBe(refusal => refusal.Code == "COHSQLT004" && refusal.Message.StartsWith("COHSQLT004", StringComparison.Ordinal));
        refusals.ShouldAllBe(refusal => refusal.Message.Contains("a write of the file header", StringComparison.Ordinal));
        StorageOfflineException.Find(refusals[0])!.Cause.ShouldBe(StorageOfflineCause.HeaderWrite);
        faults.HeaderWriteFailures.ShouldBe(1);
        failing.DataStorage.JournalLength.ShouldBe(journalAtTheFault);
        dataAfter.Data.ShouldBe(dataAtTheFault.Data);
        dataAfter.Journal.ShouldBe(dataAtTheFault.Journal);
        catalogAfter.Data.ShouldBe(catalogAtTheFault.Data);
        catalogAfter.Journal.ShouldBe(catalogAtTheFault.Journal);
        healthyCheckpointed.ShouldBeTrue();
        worker.Fault.ShouldBeNull();
        worker.FailureCount.ShouldBe(0);
        engine.State.ShouldBe(EngineState.Running);
        reopened.IsOffline.ShouldBeFalse();
        (await CountAsync(reopened)).ShouldBe(11);
    }

    /// <summary>
    /// Two writers in flight when a failed header slot write, journal fsync or journal drain takes
    /// the database offline (#1268 review): the writer holding the row lock keeps it, because an
    /// offline database undoes nothing and releasing the lock without the undo would hand the next
    /// writer versions that were never undone, so the writer queued behind it must end with the
    /// coded refusal instead. Before the review it waited until the database was reopened. The
    /// drain of the journal's append buffer (#1252) goes offline through the same hook, so it ends
    /// the wait too, with <see cref="StorageOfflineCause.JournalFlush"/> as the cause.
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
    [Theory(DisplayName = "Cohesion Test [SqlEngine] - Workers: a writer queued for the writer lock when the database goes offline ends with the coded refusal")]
    [InlineData(DeviceFault.HeaderWrite)]
    [InlineData(DeviceFault.JournalFlush)]
    [InlineData(DeviceFault.JournalWrite)]
    public async Task QueuedWriter_DatabaseGoesOffline_ShouldEndWithTheOfflineRefusal(DeviceFault fault)
    {
        // Arrange: the checkpointer looks every 100 ms; one writer holds a row lock in
        // an explicit transaction, and another queues behind it.
        var strategy = new FaultInjectingJournalSqlStorageStrategy(durable: true);
        await using var engine = SqlDatabaseEngine.Create("sql-engine", Options(strategy, checkpoint: TimeSpan.FromMilliseconds(100)));
        var failing = await CreateAsync(engine, Failing);
        var faults = strategy.Faults(Failing);
        await InsertAsync(failing, 0, 1);
        await using var holder = await failing.CreateSessionAsync();
        await using var queued = await failing.CreateSessionAsync();
        _ = await holder.BeginTransactionAsync();
        await holder.ExecuteAsync("UPDATE t SET payload = 'held' WHERE id = 0");
        var waiting = queued.ExecuteAsync("UPDATE t SET payload = 'queued' WHERE id = 0").AsTask();

        // The queued writer's transaction is open beside the holder's, and its statement has not
        // completed, on a database still online.
        await Eventually(() => waiting.IsCompleted || failing.Coordinator.GetOpenContexts().Count == 2);
        bool queuedWhileOnline = !waiting.IsCompleted && failing.Coordinator.GetOpenContexts().Count == 2 && !failing.IsOffline;
        string queuedState = $"queued {waiting.Status}, {failing.Coordinator.GetOpenContexts().Count} open contexts, offline {failing.IsOffline}";

        // Act: the next checkpoint's header slot write, its journal fsync, or the drain of the
        // journal's append buffer that leads it, fails.
        faults.SwitchOn(fault);

        // The holder writes again, so the next checkpoint is due (it may already be refused).
        await Record.ExceptionAsync(async () => await holder.ExecuteAsync("INSERT INTO t (id, payload) VALUES (2, 'held-2')"));
        bool offline = await Eventually(() => engine.OfflineDatabases.Contains((DatabaseName)Failing));
        bool ended = await Task.WhenAny(waiting, Task.Delay(TimeSpan.FromSeconds(5))) == waiting;
        var queuedRefusal = ended ? await Record.ExceptionAsync(() => waiting) : null;
        var holderRefusal = await Record.ExceptionAsync(async () => await holder.ExecuteAsync("INSERT INTO t (id, payload) VALUES (3, 'after')"));

        faults.Clear();
        var reopened = await engine.OpenDatabaseAsync(Failing);

        // Assert: both writers got the coded refusal naming what failed, and the reopen kept
        // neither write.
        queuedWhileOnline.ShouldBeTrue(queuedState);
        offline.ShouldBeTrue();
        ended.ShouldBeTrue("the queued writer was still waiting five seconds after the database went offline");
        queuedRefusal.ShouldBeOfType<DatabaseOfflineException>().Code.ShouldBe("COHSQLT004");
        StorageOfflineException.Find(queuedRefusal)!.Cause.ShouldBe(fault == DeviceFault.HeaderWrite ? StorageOfflineCause.HeaderWrite : StorageOfflineCause.JournalFlush);
        holderRefusal.ShouldBeOfType<DatabaseOfflineException>().Code.ShouldBe("COHSQLT004");
        reopened.IsOffline.ShouldBeFalse();
        (await CountAsync(reopened)).ShouldBe(1);
    }

    // Since the engine derives from DatabaseEngine (concrete-types plan, phase 4), a registered
    // worker is a DatabaseEngineWorker, whose loop lets nothing escape: the pass that throws is
    // recorded and the next one runs after the backoff. The interface-only worker this test drove
    // until then cannot be registered any more.
    [Fact(DisplayName = "Cohesion Test [SqlEngine] - Workers: a registered worker whose pass throws is run again, and the engine reports Faulted")]
    public async Task Pump_RegisteredWorkerPassThrows_ShouldRunItAgainAndReportFaulted()
    {
        // Arrange
        var worker = new EscapingWorker();
        var builder = SqlDatabaseEngine.CreateBuilder("escaping-worker");
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
    /// The checkpointer skips a database its holder closed, rather than only tolerating its
    /// disposed file sets (the closed-database fix, concrete-types plan §6.4 "Sql at P4"). A close
    /// that is not idle leaves the data journal untruncated: here another storage bracket holds
    /// every data page through the close, so the rolled-back transaction's deferred undo still
    /// fails when the close retries it, and the close keeps its writer in flight (#1226). The
    /// closed data set then stays due for a checkpoint that can never run, and the storage refuses
    /// it with <see cref="StorageTransactionException"/>, not <see cref="ObjectDisposedException"/>,
    /// so <c>SqlDatabaseEngine.IsOpen</c> does not cover it. Without the skip, the failure the
    /// checkpointer had recorded for the database never ended and the engine reported
    /// <see cref="EngineState.Faulted"/> for good; <c>SqlCheckpointWorker.IsCheckpointDue</c> is
    /// false for a closed database, so the first pass after the failure's backoff ends the record.
    /// The KeyValuePair, Graph and Documents suites carry the same test.
    /// </summary>
    [Fact(DisplayName = "Cohesion Test [SqlEngine] - Workers: a failing database closed with a writer in flight ends its checkpoint failure, and the engine runs again")]
    public async Task CheckpointWorker_FailingDatabaseClosedWithAWriterInFlight_ShouldEndItsFailureAndLeaveTheEngineRunning()
    {
        // Arrange: the checkpointer looks every 100 ms and records a failure for a database whose
        // page writes fail.
        var strategy = new FaultInjectingJournalSqlStorageStrategy(durable: true);
        await using var engine = SqlDatabaseEngine.Create("sql-engine", Options(strategy, checkpoint: TimeSpan.FromMilliseconds(100)));
        var failing = await CreateAsync(engine, Failing);
        var worker = WorkerOf(engine, DatabaseEngineWorkerKind.Checkpoint);
        var faults = strategy.Faults(Failing);
        faults.FailPageWrites = true;
        await InsertAsync(failing, 0, 20);
        bool failed = await Eventually(() => worker.ConsecutiveFailures >= 1);

        // A rolled-back transaction whose undo is deferred: another bracket holds every data page
        // through the close, so no checkpoint of the data set can run from here on either.
        await using (var session = await failing.CreateSessionAsync())
        {
            var transaction = await session.BeginTransactionAsync();
            await session.ExecuteAsync("INSERT INTO t (id, payload) VALUES (1000, 'rolled back')");
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
    /// database offline. Every operation is then refused with COHSQLT004, and once the fault clears the
    /// reopen brings the database back with its rows. Before decision 25 the worker retried it
    /// every second for as long as the process ran, and its journal grew without bound.
    /// </summary>
    [Fact(DisplayName = "Cohesion Test [Database.Sql] - Workers: a checkpoint failure that persists past the window across the minimum of passes takes only its database offline")]
    public async Task CheckpointWorker_FailurePersistsPastTheWindow_ShouldTakeOnlyItsDatabaseOffline()
    {
        // Arrange: the checkpointer looks every 100 ms; the default window of 100 s and minimum of
        // three failed passes, on a clock the test moves.
        var clock = new ManualTimeProvider();
        var strategy = new FaultInjectingJournalSqlStorageStrategy(durable: true);
        var options = Options(strategy, checkpoint: TimeSpan.FromMilliseconds(100));
        options.WorkerFailureWindow = DatabaseEngine.DefaultWorkerFailureWindow;
        options.WorkerFailureMinimumPasses = DatabaseEngine.DefaultWorkerFailureMinimumPasses;
        options.TimeProvider = clock;
        await using var engine = SqlDatabaseEngine.Create("sql-engine", options);
        var failing = await CreateAsync(engine, Failing);
        var healthy = await CreateAsync(engine, Healthy);
        var worker = WorkerOf(engine, DatabaseEngineWorkerKind.Checkpoint);
        var faults = strategy.Faults(Failing);

        // Act: every data page write of one database fails while its journal is due, for good. The
        // minimum of failed passes, a backoff apart, passes inside the window.
        faults.FailPageWrites = true;
        await InsertAsync(failing, 0, 20);
        bool failedTheMinimum = await Eventually(() => worker.FailureCount >= DatabaseEngine.DefaultWorkerFailureMinimumPasses);
        bool onlineInsideTheWindow = !failing.IsOffline && engine.OfflineDatabases.Count == 0;
        long passesInsideTheWindow = worker.FailureCount;

        // The window passes on the engine's clock; the next failed pass gives up on the database.
        clock.Advance(DatabaseEngine.DefaultWorkerFailureWindow);
        bool offline = await Eventually(() => engine.OfflineDatabases.Contains((DatabaseName)Failing));
        var refusal = await Should.ThrowAsync<DatabaseOfflineException>(async () => await failing.CreateSessionAsync());
        var error = engine.GetOfflineError(Failing);

        // The checkpointer keeps serving the other database, and the failure record ends.
        await InsertAsync(healthy, 0, 20);
        long healthyWritten = healthy.DataStorage.JournalLength;
        bool healthyCheckpointed = await Eventually(() => healthy.DataStorage.JournalLength < healthyWritten);
        bool running = await Eventually(() => worker.Fault is null && engine.State == EngineState.Running);
        long failedPasses = worker.FailureCount;
        var offlineList = engine.OfflineDatabases.ToArray();

        faults.Clear();
        var reopened = await engine.OpenDatabaseAsync(Failing);

        // Assert: online through the minimum of passes inside the window, offline on a failed pass
        // past it.
        failedTheMinimum.ShouldBeTrue();
        onlineInsideTheWindow.ShouldBeTrue();
        offline.ShouldBeTrue();
        failedPasses.ShouldBeGreaterThan(passesInsideTheWindow);
        refusal.Code.ShouldBe("COHSQLT004");
        refusal.Message.ShouldStartWith("COHSQLT004", Case.Sensitive);
        refusal.Message.ShouldContain("its checkpoints kept failing");
        var cause = StorageOfflineException.Find(refusal).ShouldNotBeNull();
        cause.Cause.ShouldBe(StorageOfflineCause.CheckpointFailures);
        cause.Message.ShouldContain($"'{worker.Name}'");
        cause.Message.ShouldContain("passes in a row over 100 s, at least the engine's window of 100 s", Case.Sensitive);
        Mentions(refusal, "Injected page write failure").ShouldBeTrue(refusal.ToString());
        error.ShouldNotBeNull().Cause.ShouldBe(StorageOfflineCause.CheckpointFailures);
        offlineList.ShouldBe([(DatabaseName)Failing]);
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
    [Fact(DisplayName = "Cohesion Test [Database.Sql] - Workers: a journal past the cap while its checkpoints fail takes only its database offline")]
    public async Task CheckpointWorker_JournalPastTheCap_ShouldTakeOnlyItsDatabaseOffline()
    {
        // Arrange: checkpoints by journal size, a cap of four times the size, and a minimum of failed
        // passes out of reach.
        var strategy = new FaultInjectingJournalSqlStorageStrategy(durable: true);
        var options = Options(strategy);
        options.CheckpointJournalSize = PaceJournalSize;
        options.JournalSizeLimit = 4 * PaceJournalSize;
        await using var engine = SqlDatabaseEngine.Create("sql-engine", options);
        var failing = await CreateAsync(engine, Failing);
        var healthy = await CreateAsync(engine, Healthy);
        var worker = (SqlCheckpointWorker)WorkerOf(engine, DatabaseEngineWorkerKind.Checkpoint);
        var passes = new ConcurrentQueue<DatabaseCheckpointPass>();
        worker.PassCompleted = passes.Enqueue;
        var faults = strategy.Faults(Failing);

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

        bool offline = await Eventually(() => engine.OfflineDatabases.Contains((DatabaseName)Failing));
        long failedPasses = worker.FailureCount;
        bool failedTwice = await Eventually(() => worker.FailureCount >= 2);
        var refusal = await Should.ThrowAsync<DatabaseOfflineException>(async () => await failing.CreateSessionAsync());

        // The other database is still checkpointed by size.
        long checkpoints = strategy.Faults(Healthy).HeaderWrites;
        for (int id = 0; strategy.Faults(Healthy).HeaderWrites == checkpoints && id < 100_000; id += 10)
        {
            await InsertAsync(healthy, id, 10);
        }

        // Assert
        passes.SelectMany(pass => pass.Visits).ShouldContain(visit => visit.Database == Failing && visit.GiveUpRequested);
        passes.SelectMany(pass => pass.Visits).ShouldContain(visit => visit.Database == Failing && visit.Offline);
        worker.PassCompleted = null;
        offline.ShouldBeTrue();
        // Long before the window: fewer failed passes than the window holds at the backoff.
        failedPasses.ShouldBeLessThan((long)(DatabaseEngine.DefaultWorkerFailureWindow / DatabaseEngineWorker.FailureBackoff));
        failedTwice.ShouldBeTrue();
        refusal.Code.ShouldBe("COHSQLT004");
        refusal.Message.ShouldContain("its journal grew past its engine's limit while its checkpoints kept failing");
        var cause = StorageOfflineException.Find(refusal).ShouldNotBeNull();
        cause.Cause.ShouldBe(StorageOfflineCause.JournalSizeLimit);
        cause.Message.ShouldContain($"past the engine's limit of {options.JournalSizeLimit} bytes");
        cause.Message.ShouldContain($"'{worker.Name}'");
        cause.Message.ShouldContain("passes in a row");
        engine.OfflineDatabases.ShouldBe([(DatabaseName)Failing]);
        healthy.IsOffline.ShouldBeFalse();
        strategy.Faults(Healthy).HeaderWrites.ShouldBeGreaterThan(checkpoints);
    }

    /// <summary>
    /// A journal that reached the engine's cap with no failure at all (a starved checkpointer,
    /// #1283) is not taken offline by one transient checkpoint failure (owner decision 25 review):
    /// the cap needs a second failed checkpoint in a row. Here the journal grows past the cap while
    /// no checkpoint is due, one checkpoint then fails, the fault clears before the worker's retry,
    /// and the retried checkpoint truncates the journal.
    /// </summary>
    [Fact(DisplayName = "Cohesion Test [Database.Sql] - Workers: one transient checkpoint failure of a journal already past the cap leaves the database online")]
    public async Task CheckpointWorker_JournalPastTheCapOneTransientFailure_ShouldLeaveTheDatabaseOnline()
    {
        // Arrange: no checkpoint is due (no size trigger, an hour's time backstop) while the journal
        // grows past a cap of four times the pace size, with no fault.
        var strategy = new FaultInjectingJournalSqlStorageStrategy(durable: true);
        var options = Options(strategy);
        options.CheckpointJournalSize = 0;
        options.JournalSizeLimit = 4 * PaceJournalSize;
        await using var engine = SqlDatabaseEngine.Create("sql-engine", options);
        var database = await CreateAsync(engine, Failing);
        var worker = WorkerOf(engine, DatabaseEngineWorkerKind.Checkpoint);
        var faults = strategy.Faults(Failing);
        for (int id = 0; database.DataStorage.JournalLength < options.JournalSizeLimit; id += 10)
        {
            await InsertAsync(database, id, 10);
        }

        long grown = database.DataStorage.JournalLength;

        // Act: the size trigger is armed and exactly one page write fails, so the first checkpoint
        // fails and the worker's retry, a backoff later, finds the fault gone. A fault the test
        // switched off after seeing the failure raced that retry: under load the retry ran first,
        // failed too, and two failed checkpoints of a journal past the cap took the database offline.
        faults.FailNextPageWrites(1);
        database.DataStorage.CheckpointJournalSize = PaceJournalSize;
        bool failed = await Eventually(() => worker.FailureCount >= 1);
        bool truncated = await Eventually(() => worker.Fault is null && database.DataStorage.JournalLength < grown);

        // Assert
        grown.ShouldBeGreaterThanOrEqualTo(options.JournalSizeLimit);
        failed.ShouldBeTrue();
        truncated.ShouldBeTrue();
        faults.PageWriteFailures.ShouldBe(1);
        worker.FailureCount.ShouldBe(1);
        database.IsOffline.ShouldBeFalse();
        engine.OfflineDatabases.ShouldBeEmpty();
        engine.GetOfflineError(Failing).ShouldBeNull();
        engine.State.ShouldBe(EngineState.Running);
    }

    /// <summary>
    /// A transient checkpoint failure leaves the database online (owner decisions 25 and 42): a
    /// streak of the minimum of failed passes ends when the fault clears and a checkpoint finishes,
    /// 60 s into the window on the engine's clock; a second streak starts the window again, so the
    /// database is never taken offline although its two streaks span 120 s of the clock.
    /// </summary>
    [Fact(DisplayName = "Cohesion Test [Database.Sql] - Workers: a transient checkpoint failure under the window leaves the database online, and the window starts again")]
    public async Task CheckpointWorker_TransientFailureUnderTheWindow_ShouldLeaveTheDatabaseOnline()
    {
        // Arrange: the checkpointer looks every 100 ms; the default window of 100 s and minimum of
        // three failed passes, on a clock the test moves.
        var clock = new ManualTimeProvider();
        var strategy = new FaultInjectingJournalSqlStorageStrategy(durable: true);
        var options = Options(strategy, checkpoint: TimeSpan.FromMilliseconds(100));
        options.WorkerFailureWindow = DatabaseEngine.DefaultWorkerFailureWindow;
        options.WorkerFailureMinimumPasses = DatabaseEngine.DefaultWorkerFailureMinimumPasses;
        options.TimeProvider = clock;
        await using var engine = SqlDatabaseEngine.Create("sql-engine", options);
        var failing = await CreateAsync(engine, Failing);
        var worker = WorkerOf(engine, DatabaseEngineWorkerKind.Checkpoint);
        var faults = strategy.Faults(Failing);

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
        engine.GetOfflineError(Failing).ShouldBeNull();
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
    [Fact(DisplayName = "Cohesion Test [Database.Sql] - Workers: a failed full version-purge pass is retried for its database alone after the backoff, and its failure ends once a retry completes")]
    public async Task VersionPurgeWorker_FullPassFails_ShouldRetryItAfterTheBackoffAndRecover()
    {
        // Arrange: a full pass due once an hour on a clock the test moves; a hook fails one
        // database's full pass until the test clears it.
        var clock = new ManualTimeProvider();
        var options = Options(new FaultInjectingJournalSqlStorageStrategy());
        options.TimeProvider = clock;
        await using var engine = SqlDatabaseEngine.Create("sql-engine", options);
        await CreateAsync(engine, Failing);
        await CreateAsync(engine, Healthy);
        var worker = (SqlVersionPurgeWorker)WorkerOf(engine, DatabaseEngineWorkerKind.VersionPurge);
        var fullPasses = new ConcurrentDictionary<string, int>(StringComparer.Ordinal);
        bool fail = true;
        worker.BeforeFullPass = database =>
        {
            fullPasses.AddOrUpdate(database.Name, 1, static (_, count) => count + 1);
            if (Volatile.Read(ref fail) && database.Name == Failing)
            {
                throw new InvalidOperationException("Injected version purge failure");
            }
        };

        // Act: the first full pass fails on one database.
        clock.Advance(options.MaintenanceInterval);
        worker.RunIteration(CancellationToken.None);
        var afterTheFailure = (Failing: fullPasses.GetValueOrDefault(Failing), Healthy: fullPasses.GetValueOrDefault(Healthy), Refused: engine.HasFailingWorker(Failing));

        // A pass before the backoff has passed does not run it again, and keeps the failure.
        worker.RunIteration(CancellationToken.None);
        var beforeTheBackoff = (Failing: fullPasses.GetValueOrDefault(Failing), Refused: engine.HasFailingWorker(Failing));

        // The backoff passes: the full pass runs again for the failing database alone, and fails.
        clock.Advance(DatabaseEngineWorker.FailureBackoff);
        worker.RunIteration(CancellationToken.None);
        var afterTheRetry = (Failing: fullPasses.GetValueOrDefault(Failing), Healthy: fullPasses.GetValueOrDefault(Healthy), Failures: worker.ConsecutiveFailures);

        // The fault clears; the next retry, a backoff later, completes.
        Volatile.Write(ref fail, false);
        clock.Advance(DatabaseEngineWorker.FailureBackoff);
        worker.RunIteration(CancellationToken.None);

        // Assert
        afterTheFailure.ShouldBe((1, 1, true));
        beforeTheBackoff.ShouldBe((1, true));
        afterTheRetry.ShouldBe((2, 1, 2));
        fullPasses.GetValueOrDefault(Failing).ShouldBe(3);
        fullPasses.GetValueOrDefault(Healthy).ShouldBe(1);
        worker.Fault.ShouldBeNull();
        worker.ConsecutiveFailures.ShouldBe(0);
        engine.HasFailingWorker(Failing).ShouldBeFalse();
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
    [Fact(DisplayName = "Cohesion Test [Database.Sql] - Workers: a full version-purge pass that keeps failing takes only its database offline at about the window, not intervals later")]
    public async Task VersionPurgeWorker_FullPassKeepsFailing_ShouldTakeOnlyItsDatabaseOfflineAtAboutTheWindow()
    {
        // Arrange: the default window of 100 s and minimum of three failed passes, and a full pass
        // due once an hour, on a clock the test moves; a hook fails one database's full pass.
        var clock = new ManualTimeProvider();
        var options = Options(new FaultInjectingJournalSqlStorageStrategy());
        options.WorkerFailureWindow = DatabaseEngine.DefaultWorkerFailureWindow;
        options.WorkerFailureMinimumPasses = DatabaseEngine.DefaultWorkerFailureMinimumPasses;
        options.TimeProvider = clock;
        await using var engine = SqlDatabaseEngine.Create("sql-engine", options);
        var failing = await CreateAsync(engine, Failing);
        var healthy = await CreateAsync(engine, Healthy);
        var worker = (SqlVersionPurgeWorker)WorkerOf(engine, DatabaseEngineWorkerKind.VersionPurge);
        worker.BeforeFullPass = database =>
        {
            if (database.Name == Failing)
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
        bool offline = await Eventually(() => engine.OfflineDatabases.Contains((DatabaseName)Failing));
        var refusal = await Should.ThrowAsync<DatabaseOfflineException>(async () => await failing.CreateSessionAsync());

        // Assert
        onlineInsideTheWindow.ShouldBeTrue();
        offline.ShouldBeTrue();
        refusal.Code.ShouldBe("COHSQLT004");
        refusal.Message.ShouldContain("its version purge kept failing");
        var cause = StorageOfflineException.Find(refusal).ShouldNotBeNull();
        cause.Cause.ShouldBe(StorageOfflineCause.VersionPurgeFailures);
        cause.Message.ShouldContain($"'{worker.Name}'");
        cause.Message.ShouldContain("on 3 passes in a row over 101 s, at least the engine's window of 100 s", Case.Sensitive);
        Mentions(refusal, "Injected version purge failure").ShouldBeTrue(refusal.ToString());
        engine.OfflineDatabases.ShouldBe([(DatabaseName)Failing]);
        healthy.IsOffline.ShouldBeFalse();
        (await CountAsync(healthy)).ShouldBe(0);
    }

    [Fact(DisplayName = "Cohesion Test [Database.Sql] - Workers: the worker failure window, its minimum of passes and the journal cap have their defaults, are validated, and reach the engine through the builder")]
    public async Task Options_WorkerLimits_ShouldDefaultValidateAndReachTheEngine()
    {
        // Arrange
        var defaults = new SqlDatabaseEngineOptions();
        var builder = SqlDatabaseEngine.CreateBuilder("worker-limits");
        builder.Options.WorkerFailureWindow = TimeSpan.FromSeconds(30);
        builder.Options.WorkerFailureMinimumPasses = 4;
        builder.Options.JournalSizeLimit = 512L * 1024 * 1024;

        // Act
        await using var engine = SqlDatabaseEngine.Create("sql-engine", new());
        await using var timeOnly = SqlDatabaseEngine.Create("sql-engine", new() { CheckpointJournalSize = 0 });
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
        Should.Throw<ArgumentOutOfRangeException>(() => SqlDatabaseEngine.Create("sql-engine", new() { WorkerFailureWindow = TimeSpan.Zero }))
            .ParamName.ShouldBe(nameof(SqlDatabaseEngineOptions.WorkerFailureWindow));
        Should.Throw<ArgumentOutOfRangeException>(() => SqlDatabaseEngine.Create("sql-engine", new() { WorkerFailureWindow = DatabaseEngine.MaximumWorkerFailureWindow + TimeSpan.FromTicks(1) }))
            .ParamName.ShouldBe(nameof(SqlDatabaseEngineOptions.WorkerFailureWindow));
        Should.Throw<ArgumentOutOfRangeException>(() => SqlDatabaseEngine.Create("sql-engine", new() { WorkerFailureMinimumPasses = 0 }))
            .ParamName.ShouldBe(nameof(SqlDatabaseEngineOptions.WorkerFailureMinimumPasses));
        Should.Throw<ArgumentOutOfRangeException>(() => SqlDatabaseEngine.Create("sql-engine", new() { JournalSizeLimit = -1 }))
            .ParamName.ShouldBe(nameof(SqlDatabaseEngineOptions.JournalSizeLimit));
        Should.Throw<ArgumentOutOfRangeException>(() => SqlDatabaseEngine.Create("sql-engine", new() { CheckpointJournalSize = 1024 * 1024, JournalSizeLimit = 1024 }))
            .ParamName.ShouldBe(nameof(SqlDatabaseEngineOptions.JournalSizeLimit));
    }

    // Journal size used to exercise checkpoint lanes and failure retries.
    private const long PaceJournalSize = 64 * 1024;

    // How long the hung-fsync test writes to the healthy database while the other one's fsync hangs.
    private static readonly TimeSpan StallWindow = TimeSpan.FromSeconds(5);

    // The worker failure minimum of passes and the journal cap are out of reach unless a test sets
    // them (owner decisions 25 and 42): the retry tests keep a database failing for seconds on purpose, and the pace
    // pace test observes its failures, retries and backoff skips.
    private static SqlDatabaseEngineOptions Options(FaultInjectingJournalSqlStorageStrategy strategy, TimeSpan? checkpoint = null, TimeSpan? writeBack = null) => new()
    {
        StorageStrategy = strategy,
        CheckpointInterval = checkpoint ?? TimeSpan.FromHours(1),
        PageWriteBackInterval = writeBack ?? TimeSpan.FromHours(1),
        MaintenanceInterval = TimeSpan.FromHours(1),
        WorkerFailureMinimumPasses = int.MaxValue,
        JournalSizeLimit = long.MaxValue,
    };

    private static DatabaseEngineWorker WorkerOf(SqlDatabaseEngine engine, DatabaseEngineWorkerKind kind)
        => engine.Workers.OfType<DatabaseEngineWorker>().Single(worker => worker.Kind == kind);

    private static async Task<SqlDatabase> CreateAsync(SqlDatabaseEngine engine, string name)
    {
        var database = await engine.CreateDatabaseAsync(name);
        await using var session = await database.CreateSessionAsync();
        await session.ExecuteAsync("CREATE TABLE t (id INT NOT NULL, payload VARCHAR(200))");
        return database;
    }

    private static async Task InsertAsync(SqlDatabase database, int first, int count)
    {
        await using var session = await database.CreateSessionAsync();
        for (int id = first; id < first + count; id++)
        {
            await session.ExecuteAsync($"INSERT INTO t (id, payload) VALUES ({id}, '{new string('x', 150)}')");
        }
    }

    private static async Task<List<TimeSpan>> TimedInsertsAsync(SqlDatabase database, int count)
    {
        var latencies = new List<TimeSpan>();
        await using var session = await database.CreateSessionAsync();
        for (int id = 0; id < count; id++)
        {
            var watch = Stopwatch.StartNew();
            await session.ExecuteAsync($"INSERT INTO t (id, payload) VALUES ({id}, 'grouped')");
            latencies.Add(watch.Elapsed);
        }

        return latencies;
    }

    private static async Task<long> CountAsync(SqlDatabase database)
    {
        await using var session = await database.CreateSessionAsync();
        var result = await session.ExecuteAsync("SELECT COUNT(*) FROM t");
        var set = result.ShouldBeAssignableTo<DatabaseResultSet>().ShouldNotBeNull();
        await using (set)
        {
            await foreach (var row in set.GetRowsAsync())
            {
                return Convert.ToInt64(row.GetValue(0));
            }
        }

        throw new InvalidOperationException("COUNT(*) returned no row.");
    }

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
            if (watch.Elapsed > Timeout)
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
