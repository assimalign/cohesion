using System;
using System.Collections.Generic;
using System.Diagnostics;
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
public sealed class SqlWorkerResilienceTests
{
    private const string Failing = "failing";
    private const string Healthy = "healthy";
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    [Fact(DisplayName = "Cohesion Test [SqlEngine] - Workers: a checkpoint whose page writes fail is retried after a backoff, and recovers when the fault clears")]
    public async Task CheckpointWorker_PageWritesFail_ShouldRetryAndRecoverWhileOtherDatabasesCheckpoint()
    {
        // Arrange: the checkpointer looks every 100 ms; nothing else writes pages back.
        var strategy = new FaultInjectingJournalSqlStorageStrategy(durable: true);
        await using var engine = SqlDatabaseEngine.Create(Options(strategy, checkpoint: TimeSpan.FromMilliseconds(100)));
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
    /// One database whose checkpoints keep failing must not slow the checkpoints of the engine's
    /// other databases (#1268 review): only the failing database is backed off. A worker-wide
    /// backoff held the healthy database to one checkpoint a second, and its journal grew to
    /// hundreds of times the trigger. The guarantee is the floor: over the window the healthy
    /// database must take more than twice the checkpoints such a backoff allows,
    /// 2 × (window / backoff + 1) = 14, which fails every worker-wide backoff or stall of one
    /// backoff a pass. A smaller slowdown can pass (the remarks).
    /// </summary>
    /// <remarks>
    /// <para>
    /// The two engines write over the same window, so whatever else the machine runs slows both
    /// alike. Measured one after the other, the parallel test run alone moved the ratio anywhere
    /// from a quarter to four times, with no fault in either window, and failed the bound on a
    /// three-core runner.
    /// </para>
    /// <para>
    /// Measured together, the share of the no-fault checkpoints still moved with the machine, not
    /// the fault. Under load an engine's checkpoints come in phases: for stretches of a few hundred
    /// milliseconds to several seconds its writer runs ahead and it takes up to ten times fewer
    /// per write, and the two engines enter those phases independently. Over a two-second window
    /// the share fell to 0.29-0.44 beside another engine's suite and to 0.37 pinned to three cores,
    /// against a bound of half. The window is now six seconds, compared second by second, so a
    /// stall of a second or two in one engine moves only the seconds it covers; but two engines in
    /// different phases for the whole window still held the median second's share at 0.28 in a
    /// parallel run of the whole suite.
    /// </para>
    /// <para>
    /// So the bound that decides comes from the worker-wide backoff's signature instead of from
    /// half: the floor the test always had. The backoff leaves the healthy database about one
    /// checkpoint a second whatever it writes, 5 to 7 in the window, where a healthy database took
    /// 105 to 3,203 in 100 runs pinned to three cores, half of them beside another engine's suite.
    /// A worker-wide backoff, a pass that stalls a backoff for each failure, and a pass that waits
    /// out the failing database's backoff instead of skipping it failed the floor in all 45 of
    /// their runs across the five engines. A smaller slowdown can pass: before storage format 3
    /// (#1253), a worker-wide pause of 250 ms after every pass left 17 to 23 checkpoints in the
    /// window and passed 34 of 65 runs on three cores, because a loaded engine with no fault drops
    /// to a few checkpoints a second itself, and no bound on a count tells the two apart. Catching
    /// it needs a deterministic signal from the worker, which is required follow-up work (the SQL
    /// engine's DESIGN.md, "Engine-owned background workers").
    /// </para>
    /// <para>
    /// The median second's share of the no-fault checkpoints is a secondary signal, asserted when
    /// at least two seconds count: it must reach a tenth, where the backoff's share is near 0.001
    /// on a three-core runner and 0.03 to 0.06 beside three busy threads. It adds little to the
    /// floor: the backoff and stall passed it in 2 of their 45 runs. A second counts only when the
    /// no-fault engine took a checkpoint in it and neither engine's writer was starved in it,
    /// writing nothing or under a tenth of its mean second. Scored as zero, the seconds in which
    /// the scheduler gave the faulted engine's writer no CPU failed a run with the engine
    /// unchanged, its writer idle four seconds of six. It is no rare stall: in 100 runs with the
    /// engine unchanged, the no-fault engine's writer went a whole second without a write in 14
    /// and the faulted engine's in 17.
    /// </para>
    /// <para>
    /// Each counted second's share is taken over the second and per write, whichever is larger.
    /// Now and then the scheduler starved one engine's writer (920 writes against 50,790 over a
    /// window pinned to three cores): its checkpointer kept up with what little was written, in
    /// far fewer checkpoints, so the count compared the writers. Per write the comparison is the
    /// checkpointers' again, except when the starved writer is the no-fault engine's: a
    /// checkpointer with little to do keeps up better per write than a busy one, and there the
    /// count is the fair one.
    /// </para>
    /// <para>
    /// The journal peak is no longer compared with the no-fault peak: over six seconds beside
    /// another suite, a healthy engine's journal peaked at 544 MB, within what the worker-wide
    /// backoff reached (470 MB to 1.1 GB), while the checkpoint counts still told the two apart by
    /// hundreds of times. It is reported.
    /// </para>
    /// <para>
    /// The window ends early once the healthy database's data file and journal hold
    /// <see cref="PaceFileBound"/> bytes together, so the test double's memory streams stay far
    /// from their 2 GiB capacity on any runner, but never before two backoffs. The window starts
    /// after the failing database's first failure, and the worker retries it a backoff later, so
    /// that retry and two seconds that can count toward the share always fall inside the window: a
    /// window the bound ended in under a second failed a healthy engine for want of a failed pass,
    /// in 3 of 10 runs with the bound forced to 4 MiB, and in none of 10 once the window ran two
    /// backoffs. An early end only shortens the time in which a worker-wide backoff takes its
    /// checkpoint a second, so the floor still exceeds twice what such a backoff allows, and the
    /// seconds after the end have no writes and do not count toward the share. The bound is a
    /// backstop, not the window's usual end: the report gives the data file's length, and says when
    /// the bound ended the window.
    /// </para>
    /// </remarks>
    [Fact(DisplayName = "Cohesion Test [SqlEngine] - Workers: a database whose checkpoints keep failing does not slow the other database's checkpoints")]
    public async Task CheckpointWorker_OneDatabaseKeepsFailing_ShouldKeepTheOthersAtFullPace()
    {
        // Act: the same load with no fault and with the failing database's page writes failing,
        // in two engines whose windows start together, on the thread pool.
        var baselineReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var faultedReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var start = Task.WhenAll(baselineReady.Task, faultedReady.Task);
        var paces = await Task.WhenAll(
            Task.Run(() => MeasureHealthyCheckpointsAsync(fault: false, baselineReady, start)),
            Task.Run(() => MeasureHealthyCheckpointsAsync(fault: true, faultedReady, start)));
        var baseline = paces[0];
        var faulted = paces[1];

        // Assert: the fault fired and was retried, and the healthy database kept a pace a
        // worker-wide backoff cannot reach: more than twice its checkpoints over the window. The
        // median counted second's share of the no-fault engine's is a secondary signal (the remarks).
        double[] shares = faulted.SharesOf(baseline);
        string report = $"no fault: {baseline}; fault: {faulted}; shares of the {shares.Length} seconds that count {string.Join(" ", shares.Select(share => $"{share:F2}"))}";
        faulted.FailedPasses.ShouldBeGreaterThanOrEqualTo(1, report);
        ((double)faulted.Checkpoints).ShouldBeGreaterThan(2 * (PaceWindow / DatabaseEngineWorker.FailureBackoff + 1), report);
        if (shares.Length >= 2)
        {
            CheckpointPace.Median(shares).ShouldBeGreaterThanOrEqualTo(PaceShareBound, report);
        }
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
        await using var engine = SqlDatabaseEngine.Create(options);
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
        await using var engine = SqlDatabaseEngine.Create(Options(strategy, writeBack: TimeSpan.FromMilliseconds(50)));
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
        await using var engine = SqlDatabaseEngine.Create(options);
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
        var reopened = (SqlDatabaseInstance)await engine.OpenDatabaseAsync(Failing);

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
        await using var engine = SqlDatabaseEngine.Create(Options(strategy, checkpoint: TimeSpan.FromMilliseconds(100)));
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
        var catalogAtTheFault = strategy.Capture(Failing + SqlDatabaseEngine.CatalogSuffix);

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
        var catalogAfter = strategy.Capture(Failing + SqlDatabaseEngine.CatalogSuffix);

        faults.Clear();
        var reopened = (SqlDatabaseInstance)await engine.OpenDatabaseAsync(Failing);

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
        await using var engine = SqlDatabaseEngine.Create(Options(strategy, checkpoint: TimeSpan.FromMilliseconds(100)));
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
        var reopened = (SqlDatabaseInstance)await engine.OpenDatabaseAsync(Failing);

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

    /// <summary>
    /// A worker registered through the builder that implements the interface without the guided
    /// base, and lets an exception escape its loop: the engine's pump runs it again after the
    /// backoff instead of letting the thread end, and reports the engine Faulted until disposal,
    /// since nothing tells it when such a worker is healthy again.
    /// </summary>
    [Fact(DisplayName = "Cohesion Test [SqlEngine] - Workers: a registered worker whose loop throws is run again, and the engine reports Faulted")]
    public async Task Pump_InterfaceWorkerThrows_ShouldRunItAgainAndReportFaulted()
    {
        // Arrange
        var worker = new EscapingWorker();
        var builder = SqlDatabaseEngine.CreateBuilder();
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

    // The checkpoint trigger, the load window of the pace test, compared second by second, and the
    // share of the no-fault checkpoints the median counted second must keep (the test's remarks).
    // The writer outpaces the checkpointer in memory, so even with no fault the journal peaks at
    // many times the trigger.
    private const long PaceJournalSize = 64 * 1024;
    private const double PaceShareBound = 0.1;
    private static readonly TimeSpan PaceWindow = TimeSpan.FromSeconds(6);
    private static readonly TimeSpan PaceSecond = TimeSpan.FromSeconds(1);

    // The most the healthy database's data file and journal hold together before the pace window
    // ends early. Each file is one MemoryStream in the test double, which cannot pass 2 GiB: while
    // every transaction took a content page of its own, a six-second window on a fast runner grew
    // the document engine's data file past that and failed its test with
    // ArgumentOutOfRangeException. A quarter of the cap keeps both files, and the memory of two
    // engines' file sets, clear of it on any runner. It is a backstop, not the window's usual end,
    // and the window runs two backoffs before it applies: passing 2 GiB in those two seconds would
    // take more than 1 GB/s.
    private const long PaceFileBound = 512L * 1024 * 1024;

    // How long the hung-fsync test writes to the healthy database while the other one's fsync hangs.
    private static readonly TimeSpan StallWindow = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Writes to the healthy database for <see cref="PaceWindow"/>, with checkpoints triggered by
    /// journal size, while the failing database's checkpoint is due and, under the fault, fails.
    /// The window starts with <paramref name="start"/>, once the engine reported itself
    /// <paramref name="ready"/>.
    /// </summary>
    private static async Task<CheckpointPace> MeasureHealthyCheckpointsAsync(bool fault, TaskCompletionSource ready, Task start)
    {
        try
        {
            var strategy = new FaultInjectingJournalSqlStorageStrategy(durable: true);
            var options = Options(strategy);
            options.CheckpointJournalSize = PaceJournalSize;
            await using var engine = SqlDatabaseEngine.Create(options);
            var failing = await CreateAsync(engine, Failing);
            var healthy = await CreateAsync(engine, Healthy);
            var worker = WorkerOf(engine, DatabaseEngineWorkerKind.Checkpoint);
            var failingFaults = strategy.Faults(Failing);
            var healthyFaults = strategy.Faults(Healthy);

            failingFaults.FailPageWrites = fault;
            try
            {
                // The failing database's journal reaches the trigger (or, with no fault, is checkpointed).
                long failingCheckpoints = failingFaults.HeaderWrites;
                for (int id = 0; failing.DataStorage.JournalLength < PaceJournalSize && failingFaults.HeaderWrites == failingCheckpoints; id += 10)
                {
                    await InsertAsync(failing, id, 10);
                }

                if (fault)
                {
                    (await Eventually(() => worker.FailureCount >= 1)).ShouldBeTrue();
                }

                // Both windows start together, each on a thread of its own.
                ready.SetResult();
                await start;
                await Task.Yield();

                long checkpoints = healthyFaults.HeaderWrites;
                long failedPasses = worker.FailureCount;
                long peak = 0;
                long writes = 0;
                var checkpointsBySecond = new long[(int)(PaceWindow / PaceSecond)];
                var writesBySecond = new long[checkpointsBySecond.Length];
                int second = 0;
                TimeSpan? endedByBound = null;
                var watch = Stopwatch.StartNew();
                for (int id = 0; watch.Elapsed < PaceWindow; id += 10)
                {
                    if (watch.Elapsed >= 2 * DatabaseEngineWorker.FailureBackoff && FileBytes(healthy) >= PaceFileBound)
                    {
                        endedByBound = watch.Elapsed;
                        break;
                    }

                    await InsertAsync(healthy, id, 10);
                    writes += 10;
                    peak = Math.Max(peak, healthy.DataStorage.JournalLength);
                    for (; second < checkpointsBySecond.Length && watch.Elapsed >= PaceSecond * (second + 1); second++)
                    {
                        checkpointsBySecond[second] = healthyFaults.HeaderWrites - checkpoints;
                        writesBySecond[second] = writes;
                    }
                }

                for (; second < checkpointsBySecond.Length; second++)
                {
                    checkpointsBySecond[second] = healthyFaults.HeaderWrites - checkpoints;
                    writesBySecond[second] = writes;
                }

                return new CheckpointPace(checkpointsBySecond, writesBySecond, peak, worker.FailureCount - failedPasses,
                    healthy.DataStorage.Data.Length, endedByBound);
            }
            finally
            {
                failingFaults.Clear();
            }
        }
        catch (Exception exception)
        {
            // An engine whose setup failed must not leave the other one's window waiting.
            ready.TrySetException(exception);
            throw;
        }
    }

    /// <summary>
    /// The healthy database's checkpoints and writes counted at the end of each second of the
    /// window, its journal peak, the worker's failed passes over the window, the healthy data
    /// file's length at the end, and when <see cref="PaceFileBound"/> ended the window, if it did.
    /// </summary>
    private sealed record CheckpointPace(long[] CheckpointsBySecond, long[] WritesBySecond, long PeakJournal, long FailedPasses,
        long DataLength, TimeSpan? EndedByBound)
    {
        /// <summary>Gets the checkpoints over the window.</summary>
        public long Checkpoints => CheckpointsBySecond[^1];

        /// <summary>
        /// Gets, for each second of the window that counts, the share of
        /// <paramref name="baseline"/>'s checkpoints this pace kept, over the second or per write,
        /// whichever is larger. A second in which the baseline took no checkpoint, or in which
        /// either engine's writer was starved (it wrote nothing, or under a tenth of its mean
        /// second), says nothing about the fault: it is left out, not scored as zero or infinity.
        /// </summary>
        /// <param name="baseline">The pace with no fault, over the same window.</param>
        /// <returns>The shares of the seconds that count, in order.</returns>
        public double[] SharesOf(CheckpointPace baseline)
        {
            double starved = WritesBySecond[^1] / (10.0 * WritesBySecond.Length);
            double baselineStarved = baseline.WritesBySecond[^1] / (10.0 * WritesBySecond.Length);
            var shares = new List<double>(CheckpointsBySecond.Length);
            for (int second = 0; second < CheckpointsBySecond.Length; second++)
            {
                long baselineCheckpoints = InSecond(baseline.CheckpointsBySecond, second);
                long baselineWrites = InSecond(baseline.WritesBySecond, second);
                long writes = InSecond(WritesBySecond, second);
                if (baselineCheckpoints == 0 || writes == 0 || writes < starved || baselineWrites < baselineStarved)
                {
                    continue;
                }

                double perSecond = (double)InSecond(CheckpointsBySecond, second) / baselineCheckpoints;
                shares.Add(Math.Max(perSecond, perSecond * baselineWrites / writes));
            }

            return [.. shares];
        }

        /// <summary>Gets the median of <paramref name="values"/>.</summary>
        /// <param name="values">The values.</param>
        /// <returns>The median.</returns>
        public static double Median(double[] values)
        {
            var sorted = values.Order().ToArray();
            int middle = sorted.Length / 2;
            return sorted.Length % 2 == 1 ? sorted[middle] : (sorted[middle - 1] + sorted[middle]) / 2;
        }

        /// <inheritdoc />
        public override string ToString()
            => $"{Checkpoints} checkpoints in {WritesBySecond[^1]} writes (by second {string.Join(" ", CheckpointsBySecond.Select((count, second) => $"{InSecond(CheckpointsBySecond, second)}/{InSecond(WritesBySecond, second)}"))}), " +
               $"journal peak {PeakJournal}, {FailedPasses} failed passes, data file {DataLength}" +
               (EndedByBound is { } ended ? $", window ended by the file bound at {ended.TotalSeconds:F1} s" : "");

        private static long InSecond(long[] counts, int second) => counts[second] - (second == 0 ? 0 : counts[second - 1]);
    }

    // What a database's two growing files hold together: the bound the pace window keeps under.
    private static long FileBytes(SqlDatabaseInstance database)
        => database.DataStorage.Data.Length + database.DataStorage.JournalLength;

    private static SqlDatabaseEngineOptions Options(FaultInjectingJournalSqlStorageStrategy strategy, TimeSpan? checkpoint = null, TimeSpan? writeBack = null) => new()
    {
        StorageStrategy = strategy,
        CheckpointInterval = checkpoint ?? TimeSpan.FromHours(1),
        PageWriteBackInterval = writeBack ?? TimeSpan.FromHours(1),
        MaintenanceInterval = TimeSpan.FromHours(1),
    };

    private static DatabaseEngineWorker WorkerOf(SqlDatabaseEngine engine, DatabaseEngineWorkerKind kind)
        => engine.Workers.OfType<DatabaseEngineWorker>().Single(worker => worker.Kind == kind);

    private static async Task<SqlDatabaseInstance> CreateAsync(SqlDatabaseEngine engine, string name)
    {
        var database = (SqlDatabaseInstance)await engine.CreateDatabaseAsync(name);
        await using var session = await database.CreateSessionAsync();
        await session.ExecuteAsync("CREATE TABLE t (id INT NOT NULL, payload VARCHAR(200))");
        return database;
    }

    private static async Task InsertAsync(SqlDatabaseInstance database, int first, int count)
    {
        await using var session = await database.CreateSessionAsync();
        for (int id = first; id < first + count; id++)
        {
            await session.ExecuteAsync($"INSERT INTO t (id, payload) VALUES ({id}, '{new string('x', 150)}')");
        }
    }

    private static async Task<List<TimeSpan>> TimedInsertsAsync(SqlDatabaseInstance database, int count)
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

    private static async Task<long> CountAsync(SqlDatabaseInstance database)
    {
        await using var session = await database.CreateSessionAsync();
        var result = await session.ExecuteAsync("SELECT COUNT(*) FROM t");
        var set = result.ShouldBeAssignableTo<QueryResultSet>().ShouldNotBeNull();
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

    /// <summary>A worker without the guided base whose first loop throws; later loops run until cancelled.</summary>
    private sealed class EscapingWorker : IDatabaseEngineWorker
    {
        private int _runs;
        private int _stopped;

        public string Name => "escaping";

        public DatabaseEngineWorkerKind Kind => DatabaseEngineWorkerKind.IndexMaintenance;

        public TimeSpan Interval => TimeSpan.FromSeconds(1);

        public int Runs => Volatile.Read(ref _runs);

        public bool Stopped => Volatile.Read(ref _stopped) != 0;

        public void Run(CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref _runs) == 1)
            {
                throw new InvalidOperationException("The worker's loop failed.");
            }

            cancellationToken.WaitHandle.WaitOne();
            Volatile.Write(ref _stopped, 1);
        }
    }
}
