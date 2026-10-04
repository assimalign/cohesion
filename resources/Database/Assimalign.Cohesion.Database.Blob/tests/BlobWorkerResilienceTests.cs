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

namespace Assimalign.Cohesion.Database.Blob.Tests;

/// <summary>
/// The blob engine's background workers under device faults that fire on their own threads
/// (#1268): a checkpoint or page write-back whose page writes fail is reported while the fault
/// lasts, retried after the worker's backoff, and recovers when the fault clears, while the
/// engine's other database keeps its work; a failed group-commit fsync and a failed header slot
/// write take only their database offline, and every later operation on it is refused with
/// COHDBB002 while nothing more is written to it.
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
    [Fact(DisplayName = "Cohesion Test [Database.Blob] - Workers: a database whose checkpoints keep failing does not slow the other database's checkpoints")]
    public async Task CheckpointWorker_OneDatabaseKeepsFailing_ShouldKeepTheOthersAtFullPace()
    {
        // Act: the same load with no fault, then with the failing database's page writes failing.
        var baseline = await MeasureHealthyCheckpointsAsync(fault: false);
        var faulted = await MeasureHealthyCheckpointsAsync(fault: true);

        // Assert: the fault fired and was retried, and the healthy database kept its pace.
        string report = $"no fault: {baseline}; fault: {faulted}";
        faulted.FailedPasses.ShouldBeGreaterThanOrEqualTo(1, report);
        ((double)faulted.Checkpoints).ShouldBeGreaterThan(2 * (PaceWindow / DatabaseEngineWorker.FailureBackoff + 1), report);
        ((double)faulted.Checkpoints / baseline.Checkpoints).ShouldBeGreaterThanOrEqualTo(0.5, report);
        ((double)faulted.PeakJournal / Math.Max(baseline.PeakJournal, PacePeakFloor)).ShouldBeLessThanOrEqualTo(16, report);
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

    [Fact(DisplayName = "Cohesion Test [Database.Blob] - Workers: a group-commit fsync failure takes only its database offline, and the flush worker keeps serving the others")]
    public async Task WriteAheadFlushWorker_FsyncFails_ShouldTakeOnlyItsDatabaseOfflineAndKeepFlushing()
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

        // Act: the failing database's journal fsync fails under the worker's group flush.
        faults.FailJournalFlushes = true;
        var watch = Stopwatch.StartNew();
        var error = await Record.ExceptionAsync(async () => await WriteAsync(files, "lost", "lost"));
        var failedCommit = watch.Elapsed;
        var refusal = await Should.ThrowAsync<DatabaseOfflineException>(async () => await failing.CreateSessionAsync());

        // The worker keeps flushing the other database's grouped commits.
        var latencies = await TimedUploadsAsync(healthy, 5);

        faults.Clear();
        var reopened = (BlobDatabaseInstance)await engine.OpenDatabaseAsync(Failing);

        // Assert
        StorageOfflineException.Find(error.ShouldNotBeNull()).ShouldNotBeNull();
        (failedCommit / window).ShouldBeLessThan(1.0);
        faults.JournalFlushFailures.ShouldBeGreaterThanOrEqualTo(1);
        faults.JournalFlushFailureThread.ShouldBe(engine.Name + "/" + DatabaseEngineWorkerKind.WriteAheadFlush);
        refusal.Code.ShouldBe("COHDBB002");
        StorageOfflineException.Find(refusal)!.Cause.ShouldBe(StorageOfflineCause.JournalFlush);
        refusal.Message.ShouldContain("a write or flush of the journal");
        StorageOfflineException.Find(refusal)!.Message.ShouldContain("a durable flush of the journal");
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
    /// Two writers in flight when a failed header slot write or journal fsync takes the database
    /// offline (#1268 review): the writer holding the database writer lock keeps it, because an
    /// offline database undoes nothing and releasing the lock without the undo would hand the next
    /// writer versions that were never undone, so the writer queued behind it must end with the
    /// coded refusal instead. Before the review it waited until the database was reopened.
    /// </summary>
    [Theory(DisplayName = "Cohesion Test [Database.Blob] - Workers: a writer queued for the writer lock when the database goes offline ends with the coded refusal")]
    [InlineData(true)]
    [InlineData(false)]
    public async Task QueuedWriter_DatabaseGoesOffline_ShouldEndWithTheOfflineRefusal(bool headerWriteFails)
    {
        // Arrange: the checkpointer looks every 100 ms; one writer holds the database writer lock in
        // an explicit transaction, and another queues behind it.
        var strategy = new FaultInjectingJournalStorageStrategy(durable: true);
        await using var engine = BlobDatabaseEngine.Create(Options(strategy, checkpoint: TimeSpan.FromMilliseconds(100)));
        var failing = await CreateAsync(engine, Failing);
        var faults = strategy.Faults(Failing);
        await using var holder = await failing.CreateSessionAsync();
        await using var queued = await failing.CreateSessionAsync();
        _ = await holder.BeginTransactionAsync();
        await WriteAsync(await ((IBlobDatabase)holder.Database).GetContainerAsync("files"), "held", "held");
        var waiting = Task.Run(async () => await WriteAsync(await ((IBlobDatabase)queued.Database).GetContainerAsync("files"), "queued", "queued"));
        await Task.Delay(TimeSpan.FromMilliseconds(100));
        bool queuedWhileOnline = !waiting.IsCompleted;

        // Act: the next checkpoint's header slot write, or its journal fsync, fails.
        if (headerWriteFails)
        {
            faults.FailHeaderWrites = true;
        }
        else
        {
            faults.FailJournalFlushes = true;
        }

        // The holder writes again, so the next checkpoint is due (it may already be refused).
        await Record.ExceptionAsync(async () => await WriteAsync(await ((IBlobDatabase)holder.Database).GetContainerAsync("files"), "held-2", "held-2"));
        bool offline = await Eventually(() => engine.OfflineDatabases.Contains((DatabaseName)Failing));
        bool ended = await Task.WhenAny(waiting, Task.Delay(TimeSpan.FromSeconds(5))) == waiting;
        var queuedRefusal = ended ? await Record.ExceptionAsync(() => waiting) : null;
        var holderRefusal = await Record.ExceptionAsync(async () => await WriteAsync(await ((IBlobDatabase)holder.Database).GetContainerAsync("files"), "after", "after"));

        faults.Clear();
        var reopened = (BlobDatabaseInstance)await engine.OpenDatabaseAsync(Failing);

        // Assert: both writers got the coded refusal, and the reopen kept neither write.
        queuedWhileOnline.ShouldBeTrue();
        offline.ShouldBeTrue();
        ended.ShouldBeTrue("the queued writer was still waiting five seconds after the database went offline");
        queuedRefusal.ShouldBeOfType<DatabaseOfflineException>().Code.ShouldBe("COHDBB002");
        holderRefusal.ShouldBeOfType<DatabaseOfflineException>().Code.ShouldBe("COHDBB002");
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

    /// <summary>
    /// Writes to the healthy database for <see cref="PaceWindow"/>, with checkpoints triggered by
    /// journal size, while the failing database's checkpoint is due and, under the fault, fails.
    /// </summary>
    private static async Task<CheckpointPace> MeasureHealthyCheckpointsAsync(bool fault)
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
