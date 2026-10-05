using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Blob.Internal;
using Assimalign.Cohesion.Database.Storage;
using Assimalign.Cohesion.Database.Storage.Tests.TestObjects;
using Assimalign.Cohesion.Database.Transactions;

namespace Assimalign.Cohesion.Database.Blob.Tests;

/// <summary>
/// The blob engine's background workers under device faults that fire on their own threads
/// (#1268): a checkpoint or page write-back whose page writes fail is reported while the fault
/// lasts, retried after the worker's backoff, and recovers when the fault clears, while the
/// engine's other database keeps its work; a failed group-commit drain (#1252) or fsync and a
/// failed header slot write take only their database offline, and every later operation on it is
/// refused with COHDBB002 while nothing more is written to it.
/// </summary>
public sealed class BlobWorkerResilienceTests
{
    private const string Failing = "failing";
    private const string Healthy = "healthy";
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    [Fact(DisplayName = "Cohesion Test [Database.Blob] - Workers: a checkpoint whose page writes fail is retried after a backoff, and recovers when the fault clears")]
    public async Task CheckpointWorker_PageWritesFail_ShouldRetryAndRecoverWhileOtherDatabasesCheckpoint()
    {
        // Arrange: the checkpointer looks every 100 ms; nothing else writes pages back.
        var strategy = new FaultInjectingJournalStorageStrategy(durable: true);
        await using var engine = BlobDatabaseEngine.Create(Options(strategy, checkpoint: TimeSpan.FromMilliseconds(100)));
        var failing = await CreateAsync(engine, Failing);
        var healthy = await CreateAsync(engine, Healthy);
        var worker = WorkerOf(engine, DatabaseEngineWorkerKind.Checkpoint);
        var workers = engine.Workers.ToArray();
        var faults = strategy.Faults(Failing);

        // Act: every data page write of one database fails while its journal is due.
        faults.FailPageWrites = true;
        var during = Stopwatch.StartNew();
        await UploadAsync(failing, 100, 20);
        bool retried = await Eventually(() => worker.ConsecutiveFailures >= 2);
        var stateDuringTheFault = engine.State;
        var fault = worker.Fault;

        // The other database's checkpoints go on meanwhile.
        await UploadAsync(healthy, 100, 20);
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
    /// One database whose checkpoints keep failing must not slow the checkpoints of the engine's
    /// other databases (#1268 review): only the failing database is backed off. Against the same
    /// load with no fault, the healthy database keeps at least half its checkpoints and its journal
    /// stays within a small multiple of the no-fault peak. A worker-wide backoff held it to one
    /// checkpoint a second, and its journal grew to hundreds of times the trigger.
    /// </summary>
    /// <remarks>
    /// The two engines write over the same window, so whatever else the machine runs slows both
    /// alike. Measured one after the other, the parallel test run alone moved the ratio anywhere
    /// from a quarter to four times, with no fault in either window, and failed the bound on a
    /// three-core runner.
    /// </remarks>
    [Fact(DisplayName = "Cohesion Test [Database.Blob] - Workers: a database whose checkpoints keep failing does not slow the other database's checkpoints")]
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

        // Assert: the fault fired and was retried, and the healthy database kept its pace.
        string report = $"no fault: {baseline}; fault: {faulted}";
        faulted.FailedPasses.ShouldBeGreaterThanOrEqualTo(1, report);
        ((double)faulted.Checkpoints).ShouldBeGreaterThan(2 * (PaceWindow / DatabaseEngineWorker.FailureBackoff + 1), report);
        ((double)faulted.Checkpoints / baseline.Checkpoints).ShouldBeGreaterThanOrEqualTo(0.5, report);
        ((double)faulted.PeakJournal / Math.Max(baseline.PeakJournal, PacePeakFloor)).ShouldBeLessThanOrEqualTo(16, report);
    }

    /// <summary>
    /// One database's checkpoint that hangs in a durable flush of its data file — an fsync its
    /// device does not answer — must not hold back the checkpoints of the engine's other databases.
    /// Before the checkpoint lanes, the worker visited the databases one by one on its own thread, so
    /// the hung fsync stopped every checkpoint of the engine until it returned: a device that took
    /// seconds to answer, or to fail, stalled every other database's journal truncation for as long.
    /// </summary>
    [Fact(DisplayName = "Cohesion Test [Database.Blob] - Workers: a database whose checkpoint fsync hangs does not hold back the other database's checkpoints")]
    public async Task CheckpointWorker_OneDatabaseFsyncHangs_ShouldKeepCheckpointingTheOthers()
    {
        // Arrange: checkpoints by journal size; nothing else writes pages back.
        var strategy = new FaultInjectingJournalStorageStrategy(durable: true);
        var options = Options(strategy);
        options.CheckpointJournalSize = PaceJournalSize;
        await using var engine = BlobDatabaseEngine.Create(options);
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
                await UploadAsync(stalled, id, 10);
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
                await UploadAsync(healthy, id, 10);
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

    [Fact(DisplayName = "Cohesion Test [Database.Blob] - Workers: a page write-back that fails is retried after a backoff, and the pages reach the file once the fault clears")]
    public async Task PageWriteBackWorker_PageWritesFail_ShouldRetryAndRecoverWhileOtherDatabasesAreWritten()
    {
        // Arrange: the page writer runs every 50 ms; no checkpoint writes pages.
        var strategy = new FaultInjectingJournalStorageStrategy(durable: true);
        await using var engine = BlobDatabaseEngine.Create(Options(strategy, writeBack: TimeSpan.FromMilliseconds(50)));
        var failing = await CreateAsync(engine, Failing);
        var healthy = await CreateAsync(engine, Healthy);
        var worker = WorkerOf(engine, DatabaseEngineWorkerKind.PageWriteBack);
        var faults = strategy.Faults(Failing);
        var healthyFaults = strategy.Faults(Healthy);

        // Act: the failing database's page writes fail while it has dirty pages.
        faults.FailPageWrites = true;
        var during = Stopwatch.StartNew();
        await UploadAsync(failing, 100, 20);
        bool retried = await Eventually(() => worker.ConsecutiveFailures >= 2);
        var stateDuringTheFault = engine.State;
        var fault = worker.Fault;

        // The other database's pages are written back meanwhile.
        long healthyWrites = healthyFaults.PageWrites;
        await UploadAsync(healthy, 100, 20);
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
    [Theory(DisplayName = "Cohesion Test [Database.Blob] - Workers: a group-commit drain or fsync failure takes only its database offline, and the flush worker keeps serving the others")]
    [InlineData(DeviceFault.JournalFlush)]
    [InlineData(DeviceFault.JournalWrite)]
    public async Task WriteAheadFlushWorker_JournalFails_ShouldTakeOnlyItsDatabaseOfflineAndKeepFlushing(DeviceFault fault)
    {
        // Arrange: grouped commits wait up to two seconds for the flush worker before they flush
        // themselves, so a commit that returns sooner was flushed by the worker.
        var window = TimeSpan.FromSeconds(2);
        var strategy = new FaultInjectingJournalStorageStrategy(durable: true);
        var options = Options(strategy);
        options.Durability = StorageCommitDurability.Grouped;
        options.GroupCommitWindow = window;
        await using var engine = BlobDatabaseEngine.Create(options);
        var failing = await CreateAsync(engine, Failing);
        var healthy = await CreateAsync(engine, Healthy);
        var worker = WorkerOf(engine, DatabaseEngineWorkerKind.WriteAheadFlush);
        var faults = strategy.Faults(Failing);
        await using var session = await failing.CreateSessionAsync();
        var files = await ((IBlobDatabase)session.Database).GetContainerAsync("files");

        // Act: the failing database's journal drain, or its fsync, fails under the worker's group
        // flush.
        faults.SwitchOn(fault);
        var watch = Stopwatch.StartNew();
        var error = await Record.ExceptionAsync(async () => await WriteAsync(files, "lost", "lost"));
        var failedCommit = watch.Elapsed;
        var refusal = await Should.ThrowAsync<DatabaseOfflineException>(async () => await failing.CreateSessionAsync());

        // The worker keeps flushing the other database's grouped commits.
        var latencies = await TimedUploadsAsync(healthy, 5);

        faults.Clear();
        var reopened = (BlobDatabaseInstance)await engine.OpenDatabaseAsync(Failing);

        // Assert: a failed drain ends the group flush before its fsync.
        bool drain = fault == DeviceFault.JournalWrite;
        StorageOfflineException.Find(error.ShouldNotBeNull()).ShouldNotBeNull();
        (failedCommit / window).ShouldBeLessThan(1.0);
        (drain ? faults.JournalWriteFailures : faults.JournalFlushFailures).ShouldBeGreaterThanOrEqualTo(1);
        (drain ? faults.JournalWriteFailureThread : faults.JournalFlushFailureThread).ShouldBe(engine.Name + "/" + DatabaseEngineWorkerKind.WriteAheadFlush);
        (drain ? faults.JournalFlushFailures : faults.JournalWriteFailures).ShouldBe(0);
        refusal.Code.ShouldBe("COHDBB002");
        StorageOfflineException.Find(refusal)!.Cause.ShouldBe(StorageOfflineCause.JournalFlush);
        refusal.Message.ShouldContain("a write or flush of the journal");
        StorageOfflineException.Find(refusal)!.Message.ShouldContain(drain ? "a write of the journal" : "a durable flush of the journal");
        (latencies.Max() / window).ShouldBeLessThan(1.0, $"grouped commit latencies {string.Join(", ", latencies)}");
        worker.Fault.ShouldBeNull();
        worker.FailureCount.ShouldBe(0);
        engine.State.ShouldBe(EngineState.Running);
        reopened.IsOffline.ShouldBeFalse();
        engine.OfflineDatabases.ShouldBeEmpty();
    }

    [Fact(DisplayName = "Cohesion Test [Database.Blob] - Workers: a failed header slot write takes the database offline: later operations are refused and its files stop growing")]
    public async Task CheckpointWorker_HeaderWriteFails_ShouldTakeTheDatabaseOfflineUntilReopened()
    {
        // Arrange
        var strategy = new FaultInjectingJournalStorageStrategy(durable: true);
        await using var engine = BlobDatabaseEngine.Create(Options(strategy, checkpoint: TimeSpan.FromMilliseconds(100)));
        var failing = await CreateAsync(engine, Failing);
        var healthy = await CreateAsync(engine, Healthy);
        await UploadAsync(failing, 0, 10);
        var worker = WorkerOf(engine, DatabaseEngineWorkerKind.Checkpoint);
        var faults = strategy.Faults(Failing);
        await using var session = await failing.CreateSessionAsync();
        var files = await ((IBlobDatabase)session.Database).GetContainerAsync("files");

        // Act: the next checkpoint's header slot write fails.
        faults.FailHeaderWrites = true;
        await WriteAsync(files, "k10", "before the fault");
        bool offline = await Eventually(() => engine.OfflineDatabases.Contains((DatabaseName)Failing));
        long journalAtTheFault = failing.DataStorage.JournalLength;
        var atTheFault = strategy.Capture(Failing);

        var refusals = new List<DatabaseOfflineException>
        {
            await Should.ThrowAsync<DatabaseOfflineException>(async () => await failing.CreateSessionAsync()),
            await Should.ThrowAsync<DatabaseOfflineException>(async () => await files.OpenWriteAsync("k11")),
            await Should.ThrowAsync<DatabaseOfflineException>(async () => await session.BeginTransactionAsync()),
        };

        // The checkpoint worker keeps visiting the engine: the healthy database is checkpointed.
        await UploadAsync(healthy, 0, 10);
        long healthyWritten = healthy.DataStorage.JournalLength;
        bool healthyCheckpointed = await Eventually(() => healthy.DataStorage.JournalLength < healthyWritten);
        var after = strategy.Capture(Failing);

        faults.Clear();
        var reopened = (BlobDatabaseInstance)await engine.OpenDatabaseAsync(Failing);

        // Assert
        offline.ShouldBeTrue();
        refusals.ShouldAllBe(refusal => refusal.Code == "COHDBB002" && refusal.Message.StartsWith("COHDBB002", StringComparison.Ordinal));
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
    /// The queued session looks its container up before the fault, and its write is started on the
    /// test's own flow. The lookup is an autocommit read of its own, whose commit appends a commit
    /// record and drains the journal for it. Inside a <c>Task.Run</c> body, as the test had it, the
    /// lookup committed after the fault whenever the thread pool started that body more than the
    /// 100 ms the test waited late (a three-core macOS runner under the whole suite): its drain was
    /// the first to fail, and the queued session got
    /// <see cref="DatabaseTransactionCommitUnconfirmedException"/>, which is right for a commit whose
    /// record was appended before its flush failed (#1243), while its write never reached the lock.
    /// <see cref="IBlobContainer.OpenWriteAsync"/> returns only once its request waits for the lock:
    /// the operation's begin and every step before the wait complete synchronously and append only
    /// its begin record, so the writer is queued, and nothing of its own drains, before the fault
    /// switches on.
    /// </remarks>
    [Theory(DisplayName = "Cohesion Test [Database.Blob] - Workers: a writer queued for the writer lock when the database goes offline ends with the coded refusal")]
    [InlineData(DeviceFault.HeaderWrite)]
    [InlineData(DeviceFault.JournalFlush)]
    [InlineData(DeviceFault.JournalWrite)]
    public async Task QueuedWriter_DatabaseGoesOffline_ShouldEndWithTheOfflineRefusal(DeviceFault fault)
    {
        // Arrange: the checkpointer looks every 100 ms; one writer holds the database writer lock in
        // an explicit transaction, and another queues behind it.
        var strategy = new FaultInjectingJournalStorageStrategy(durable: true);
        await using var engine = BlobDatabaseEngine.Create(Options(strategy, checkpoint: TimeSpan.FromMilliseconds(100)));
        var failing = await CreateAsync(engine, Failing);
        var faults = strategy.Faults(Failing);
        await using var holder = await failing.CreateSessionAsync();
        await using var queued = await failing.CreateSessionAsync();
        var queuedFiles = await ((IBlobDatabase)queued.Database).GetContainerAsync("files");
        _ = await holder.BeginTransactionAsync();
        await WriteAsync(await ((IBlobDatabase)holder.Database).GetContainerAsync("files"), "held", "held");
        var waiting = WriteAsync(queuedFiles, "queued", "queued");
        bool queuedWhileOnline = !waiting.IsCompleted && failing.Coordinator.GetOpenContexts().Count == 2;

        // Act: the next checkpoint's header slot write, its journal fsync, or the drain of the
        // journal's append buffer that leads it, fails.
        faults.SwitchOn(fault);

        // The holder writes again, so the next checkpoint is due (it may already be refused).
        await Record.ExceptionAsync(async () => await WriteAsync(await ((IBlobDatabase)holder.Database).GetContainerAsync("files"), "held-2", "held-2"));
        bool offline = await Eventually(() => engine.OfflineDatabases.Contains((DatabaseName)Failing));
        bool ended = await Task.WhenAny(waiting, Task.Delay(TimeSpan.FromSeconds(5))) == waiting;
        var queuedRefusal = ended ? await Record.ExceptionAsync(() => waiting) : null;
        var holderRefusal = await Record.ExceptionAsync(async () => await WriteAsync(await ((IBlobDatabase)holder.Database).GetContainerAsync("files"), "after", "after"));

        faults.Clear();
        var reopened = (BlobDatabaseInstance)await engine.OpenDatabaseAsync(Failing);

        // Assert: both writers got the coded refusal naming what failed, and the reopen kept
        // neither write.
        queuedWhileOnline.ShouldBeTrue();
        offline.ShouldBeTrue();
        ended.ShouldBeTrue("the queued writer was still waiting five seconds after the database went offline");
        queuedRefusal.ShouldBeOfType<DatabaseOfflineException>().Code.ShouldBe("COHDBB002");
        StorageOfflineException.Find(queuedRefusal)!.Cause.ShouldBe(fault == DeviceFault.HeaderWrite ? StorageOfflineCause.HeaderWrite : StorageOfflineCause.JournalFlush);
        holderRefusal.ShouldBeOfType<DatabaseOfflineException>().Code.ShouldBe("COHDBB002");
        reopened.IsOffline.ShouldBeFalse();
        (await CountAsync(reopened)).ShouldBe(0);
    }

    /// <summary>
    /// The holder's own upload takes the database offline: its chunks overflow the journal's
    /// append buffer (#1252), whose drain fails. The failed operation aborts the holder's explicit
    /// transaction (#1225), but an offline database undoes nothing (#1268), so the abort rolls
    /// nothing back and releases no lock: the holder keeps the database writer lock, and the writer
    /// queued behind it ends with the coded refusal instead of being granted the lock. The queued
    /// writer is started after the fault switched on, the latest it can start, and still drains
    /// nothing before it waits, so the holder's drain is the one failure. Nothing else drains: the
    /// workers' intervals are an hour.
    /// </summary>
    [Fact(DisplayName = "Cohesion Test [Database.Blob] - Workers: a holder whose upload's journal drain fails keeps the writer lock through its abort, and the queued writer gets the coded refusal")]
    public async Task QueuedWriter_HolderUploadDrainFails_ShouldKeepTheHolderLockAndRefuseTheQueuedWriter()
    {
        // Arrange: one writer holds the database writer lock in an explicit transaction; the other
        // session has looked its container up, and every journal write fails from now on.
        var strategy = new FaultInjectingJournalStorageStrategy(durable: true);
        await using var engine = BlobDatabaseEngine.Create(Options(strategy));
        var failing = await CreateAsync(engine, Failing);
        var faults = strategy.Faults(Failing);
        await using var holder = await failing.CreateSessionAsync();
        await using var queued = await failing.CreateSessionAsync();
        var queuedFiles = await ((IBlobDatabase)queued.Database).GetContainerAsync("files");
        var transaction = await holder.BeginTransactionAsync();
        var holderFiles = await ((IBlobDatabase)holder.Database).GetContainerAsync("files");
        await WriteAsync(holderFiles, "held", "held");
        var holderSequence = ((BlobDatabaseTransaction)transaction).Context.Sequence;
        faults.SwitchOn(DeviceFault.JournalWrite);

        var waiting = WriteAsync(queuedFiles, "queued", "queued");
        bool queuedWithoutADrain = !waiting.IsCompleted && failing.Coordinator.GetOpenContexts().Count == 2
            && faults.JournalWriteFailures == 0 && !failing.IsOffline;

        // Act: the holder uploads more than the append buffer holds. The bytes are not zero: since
        // #1253 a fresh page's pre-image is zero and the journal records only bytes that differ,
        // so a zero upload would journal almost nothing and never reach the failing drain.
        var payload = new byte[4 * 1024 * 1024];
        new Random(1268).NextBytes(payload);
        var holderRefusal = await Record.ExceptionAsync(async () =>
        {
            await using var stream = await holderFiles.OpenWriteAsync("large");
            await stream.WriteAsync(payload);
        });
        bool ended = await Task.WhenAny(waiting, Task.Delay(TimeSpan.FromSeconds(5))) == waiting;
        var queuedRefusal = ended ? await Record.ExceptionAsync(() => waiting) : null;

        // The database writer lock is still the holder's: another owner cannot take it, the
        // holder's own request is a re-grant.
        var locks = failing.Coordinator.LockManager;
        bool heldByAnother = !locks.TryAcquire(new TransactionSequence(ulong.MaxValue), LockResource.Database(), LockMode.Exclusive);
        bool heldByTheHolder = locks.TryAcquire(holderSequence, LockResource.Database(), LockMode.Exclusive);
        var holderState = transaction.State;

        faults.Clear();
        var reopened = (BlobDatabaseInstance)await engine.OpenDatabaseAsync(Failing);

        // Assert
        queuedWithoutADrain.ShouldBeTrue();
        holderRefusal.ShouldBeOfType<DatabaseOfflineException>().Code.ShouldBe("COHDBB002");
        StorageOfflineException.Find(holderRefusal)!.Cause.ShouldBe(StorageOfflineCause.JournalFlush);
        faults.JournalWriteFailures.ShouldBe(1);
        holderState.ShouldBe(TransactionState.Faulted);
        ended.ShouldBeTrue("the queued writer was still waiting five seconds after the database went offline");
        queuedRefusal.ShouldBeOfType<DatabaseOfflineException>().Code.ShouldBe("COHDBB002");
        StorageOfflineException.Find(queuedRefusal)!.Cause.ShouldBe(StorageOfflineCause.JournalFlush);
        heldByAnother.ShouldBeTrue();
        heldByTheHolder.ShouldBeTrue();
        reopened.IsOffline.ShouldBeFalse();
        (await CountAsync(reopened)).ShouldBe(0);
    }

    [Fact(DisplayName = "Cohesion Test [Database.Blob] - Workers: a registered worker whose loop throws is run again, and the engine reports Faulted")]
    public async Task Pump_InterfaceWorkerThrows_ShouldRunItAgainAndReportFaulted()
    {
        // Arrange
        var worker = new EscapingWorker();
        var builder = BlobDatabaseEngine.CreateBuilder();
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

    // The checkpoint trigger and the load window of the pace test. The writer outpaces the
    // checkpointer in memory, so even with no fault the journal peaks at many times the trigger;
    // the peak is compared with the no-fault peak, floored at 1 MiB so a quiet baseline run does
    // not make the bound tighter than the noise. A worker-wide backoff peaked above 150 MB.
    private const long PacePeakFloor = 1024 * 1024;
    private const long PaceJournalSize = 64 * 1024;
    private static readonly TimeSpan PaceWindow = TimeSpan.FromSeconds(2);

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
            var strategy = new FaultInjectingJournalStorageStrategy(durable: true);
            var options = Options(strategy);
            options.CheckpointJournalSize = PaceJournalSize;
            await using var engine = BlobDatabaseEngine.Create(options);
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
                    await UploadAsync(failing, id, 10);
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
                var watch = Stopwatch.StartNew();
                for (int id = 0; watch.Elapsed < PaceWindow; id += 10)
                {
                    await UploadAsync(healthy, id, 10);
                    peak = Math.Max(peak, healthy.DataStorage.JournalLength);
                }

                return new CheckpointPace(healthyFaults.HeaderWrites - checkpoints, peak, worker.FailureCount - failedPasses);
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

    /// <summary>The healthy database's checkpoints and journal peak over the window, and the worker's failed passes.</summary>
    private readonly record struct CheckpointPace(long Checkpoints, long PeakJournal, long FailedPasses);

    private static BlobDatabaseEngineOptions Options(FaultInjectingJournalStorageStrategy strategy, TimeSpan? checkpoint = null, TimeSpan? writeBack = null) => new()
    {
        StorageStrategy = strategy,
        CheckpointInterval = checkpoint ?? TimeSpan.FromHours(1),
        PageWriteBackInterval = writeBack ?? TimeSpan.FromHours(1),
        MaintenanceInterval = TimeSpan.FromHours(1),
    };

    private static DatabaseEngineWorker WorkerOf(BlobDatabaseEngine engine, DatabaseEngineWorkerKind kind)
        => engine.Workers.OfType<DatabaseEngineWorker>().Single(worker => worker.Kind == kind);

    private static async Task<BlobDatabaseInstance> CreateAsync(BlobDatabaseEngine engine, string name)
    {
        var database = (BlobDatabaseInstance)await engine.CreateDatabaseAsync(name);
        await database.CreateContainerAsync("files");
        return database;
    }

    private static async Task WriteAsync(IBlobContainer container, string name, string content)
    {
        await using var stream = await container.OpenWriteAsync(name);
        await stream.WriteAsync(Encoding.UTF8.GetBytes(content));
    }

    private static async Task UploadAsync(BlobDatabaseInstance database, int first, int count)
    {
        await using var session = await database.CreateSessionAsync();
        var files = await ((IBlobDatabase)session.Database).GetContainerAsync("files");
        for (int id = first; id < first + count; id++)
        {
            await WriteAsync(files, $"k{id}", new string('x', 2048));
        }
    }

    private static async Task<List<TimeSpan>> TimedUploadsAsync(BlobDatabaseInstance database, int count)
    {
        var latencies = new List<TimeSpan>();
        await using var session = await database.CreateSessionAsync();
        var files = await ((IBlobDatabase)session.Database).GetContainerAsync("files");
        for (int id = 0; id < count; id++)
        {
            var watch = Stopwatch.StartNew();
            await WriteAsync(files, $"grouped-{id}", "grouped");
            latencies.Add(watch.Elapsed);
        }

        return latencies;
    }

    private static async Task<int> CountAsync(BlobDatabaseInstance database)
    {
        await using var session = await database.CreateSessionAsync();
        var files = await ((IBlobDatabase)session.Database).GetContainerAsync("files");
        int count = 0;
        await foreach (var _ in files.GetBlobsAsync())
        {
            count++;
        }

        return count;
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
