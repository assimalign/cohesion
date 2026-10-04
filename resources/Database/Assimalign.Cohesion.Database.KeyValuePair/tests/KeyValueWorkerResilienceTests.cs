using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.KeyValuePair.Internal;
using Assimalign.Cohesion.Database.Storage;

namespace Assimalign.Cohesion.Database.KeyValuePair.Tests;

using static KeyValueTestHarness;

/// <summary>
/// The key-value engine's background workers under device faults that fire on their own threads
/// (#1268): a checkpoint or page write-back whose page writes fail is reported while the fault
/// lasts, retried after the worker's backoff, and recovers when the fault clears, while the
/// engine's other database keeps its work; a failed group-commit fsync and a failed header slot
/// write take only their database offline, and every later operation on it is refused with
/// COHDBK002 while nothing more is written to it.
/// </summary>
public sealed class KeyValueWorkerResilienceTests
{
    private const string Failing = "failing";
    private const string Healthy = "healthy";
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    [Fact(DisplayName = "Cohesion Test [Database.KeyValuePair] - Workers: a checkpoint whose page writes fail is retried after a backoff, and recovers when the fault clears")]
    public async Task CheckpointWorker_PageWritesFail_ShouldRetryAndRecoverWhileOtherDatabasesCheckpoint()
    {
        // Arrange: the checkpointer looks every 100 ms; nothing else writes pages back.
        var strategy = new FaultInjectingJournalStorageStrategy(durable: true);
        await using var engine = KeyValueDatabaseEngine.Create(Options(strategy, checkpoint: TimeSpan.FromMilliseconds(100)));
        var failing = (KeyValueDatabaseInstance)await engine.CreateDatabaseAsync(Failing);
        var healthy = (KeyValueDatabaseInstance)await engine.CreateDatabaseAsync(Healthy);
        var worker = WorkerOf(engine, DatabaseEngineWorkerKind.Checkpoint);
        var workers = engine.Workers.ToArray();
        var faults = strategy.Faults(Failing);

        // Act: every data page write of one database fails while its journal is due.
        faults.FailPageWrites = true;
        var during = Stopwatch.StartNew();
        await PutAsync(failing, 100, 20);
        bool retried = await Eventually(() => worker.ConsecutiveFailures >= 2);
        var stateDuringTheFault = engine.State;
        var fault = worker.Fault;

        // The other database's checkpoints go on meanwhile.
        await PutAsync(healthy, 100, 20);
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

    [Fact(DisplayName = "Cohesion Test [Database.KeyValuePair] - Workers: a page write-back that fails is retried after a backoff, and the pages reach the file once the fault clears")]
    public async Task PageWriteBackWorker_PageWritesFail_ShouldRetryAndRecoverWhileOtherDatabasesAreWritten()
    {
        // Arrange: the page writer runs every 50 ms; no checkpoint writes pages.
        var strategy = new FaultInjectingJournalStorageStrategy(durable: true);
        await using var engine = KeyValueDatabaseEngine.Create(Options(strategy, writeBack: TimeSpan.FromMilliseconds(50)));
        var failing = (KeyValueDatabaseInstance)await engine.CreateDatabaseAsync(Failing);
        var healthy = (KeyValueDatabaseInstance)await engine.CreateDatabaseAsync(Healthy);
        var worker = WorkerOf(engine, DatabaseEngineWorkerKind.PageWriteBack);
        var faults = strategy.Faults(Failing);
        var healthyFaults = strategy.Faults(Healthy);

        // Act: the failing database's page writes fail while it has dirty pages.
        faults.FailPageWrites = true;
        var during = Stopwatch.StartNew();
        await PutAsync(failing, 100, 20);
        bool retried = await Eventually(() => worker.ConsecutiveFailures >= 2);
        var stateDuringTheFault = engine.State;
        var fault = worker.Fault;

        // The other database's pages are written back meanwhile.
        long healthyWrites = healthyFaults.PageWrites;
        await PutAsync(healthy, 100, 20);
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

    [Fact(DisplayName = "Cohesion Test [Database.KeyValuePair] - Workers: a group-commit fsync failure takes only its database offline, and the flush worker keeps serving the others")]
    public async Task WriteAheadFlushWorker_FsyncFails_ShouldTakeOnlyItsDatabaseOfflineAndKeepFlushing()
    {
        // Arrange: grouped commits wait up to two seconds for the flush worker before they flush
        // themselves, so a commit that returns sooner was flushed by the worker.
        var window = TimeSpan.FromSeconds(2);
        var strategy = new FaultInjectingJournalStorageStrategy(durable: true);
        var options = Options(strategy);
        options.Durability = StorageCommitDurability.Grouped;
        options.GroupCommitWindow = window;
        await using var engine = KeyValueDatabaseEngine.Create(options);
        var failing = (KeyValueDatabaseInstance)await engine.CreateDatabaseAsync(Failing);
        var healthy = (KeyValueDatabaseInstance)await engine.CreateDatabaseAsync(Healthy);
        var worker = WorkerOf(engine, DatabaseEngineWorkerKind.WriteAheadFlush);
        var faults = strategy.Faults(Failing);
        await using var session = await failing.CreateSessionAsync();

        // Act: the failing database's journal fsync fails under the worker's group flush.
        faults.FailJournalFlushes = true;
        var watch = Stopwatch.StartNew();
        var error = await Record.ExceptionAsync(async () => await failing.PutAsync(session, Bytes("lost"), Bytes("1")));
        var failedCommit = watch.Elapsed;
        var refusal = await Should.ThrowAsync<DatabaseOfflineException>(async () => await failing.CreateSessionAsync());

        // The worker keeps flushing the other database's grouped commits.
        var latencies = await TimedPutsAsync(healthy, 5);

        faults.Clear();
        var reopened = (KeyValueDatabaseInstance)await engine.OpenDatabaseAsync(Failing);

        // Assert
        StorageOfflineException.Find(error.ShouldNotBeNull()).ShouldNotBeNull();
        (failedCommit / window).ShouldBeLessThan(1.0);
        faults.JournalFlushFailures.ShouldBeGreaterThanOrEqualTo(1);
        faults.JournalFlushFailureThread.ShouldBe(worker.Name);
        refusal.Code.ShouldBe("COHDBK002");
        StorageOfflineException.Find(refusal)!.FailedOperation.ShouldBe("a durable flush of the journal");
        refusal.Message.ShouldContain("a durable flush of the journal");
        (latencies.Max() / window).ShouldBeLessThan(1.0, $"grouped commit latencies {string.Join(", ", latencies)}");
        worker.Fault.ShouldBeNull();
        worker.FailureCount.ShouldBe(0);
        engine.State.ShouldBe(EngineState.Running);
        reopened.IsOffline.ShouldBeFalse();
        engine.OfflineDatabases.ShouldBeEmpty();
    }

    [Fact(DisplayName = "Cohesion Test [Database.KeyValuePair] - Workers: a failed header slot write takes the database offline: later operations are refused and its files stop growing")]
    public async Task CheckpointWorker_HeaderWriteFails_ShouldTakeTheDatabaseOfflineUntilReopened()
    {
        // Arrange
        var strategy = new FaultInjectingJournalStorageStrategy(durable: true);
        await using var engine = KeyValueDatabaseEngine.Create(Options(strategy, checkpoint: TimeSpan.FromMilliseconds(100)));
        var failing = (KeyValueDatabaseInstance)await engine.CreateDatabaseAsync(Failing);
        var healthy = (KeyValueDatabaseInstance)await engine.CreateDatabaseAsync(Healthy);
        await PutAsync(failing, 0, 10);
        var worker = WorkerOf(engine, DatabaseEngineWorkerKind.Checkpoint);
        var faults = strategy.Faults(Failing);
        await using var session = await failing.CreateSessionAsync();

        // Act: the next checkpoint's header slot write fails.
        faults.FailHeaderWrites = true;
        await failing.PutAsync(session, Bytes("k10"), Bytes("before the fault"));
        bool offline = await Eventually(() => engine.OfflineDatabases.Contains((DatabaseName)Failing));
        long journalAtTheFault = failing.DataStorage.JournalLength;
        var dataAtTheFault = strategy.Capture(Failing);
        var catalogAtTheFault = strategy.Capture(Failing + KeyValueDatabaseEngine.CatalogSuffix);

        var refusals = new List<DatabaseOfflineException>
        {
            await Should.ThrowAsync<DatabaseOfflineException>(async () => await failing.CreateSessionAsync()),
            await Should.ThrowAsync<DatabaseOfflineException>(async () => await failing.PutAsync(session, Bytes("k11"), Bytes("refused"))),
            await Should.ThrowAsync<DatabaseOfflineException>(async () => await session.BeginTransactionAsync()),
        };

        // The checkpoint worker keeps visiting the engine: the healthy database is checkpointed.
        await PutAsync(healthy, 0, 10);
        long healthyWritten = healthy.DataStorage.JournalLength;
        bool healthyCheckpointed = await Eventually(() => healthy.DataStorage.JournalLength < healthyWritten);
        var dataAfter = strategy.Capture(Failing);
        var catalogAfter = strategy.Capture(Failing + KeyValueDatabaseEngine.CatalogSuffix);

        faults.Clear();
        var reopened = (KeyValueDatabaseInstance)await engine.OpenDatabaseAsync(Failing);

        // Assert
        offline.ShouldBeTrue();
        refusals.ShouldAllBe(refusal => refusal.Code == "COHDBK002" && refusal.Message.StartsWith("COHDBK002", StringComparison.Ordinal));
        refusals.ShouldAllBe(refusal => refusal.Message.Contains("a write of the file header", StringComparison.Ordinal));
        StorageOfflineException.Find(refusals[0])!.FailedOperation.ShouldBe("a write of the file header");
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

    [Fact(DisplayName = "Cohesion Test [Database.KeyValuePair] - Workers: a registered worker whose loop throws is run again, and the engine reports Faulted")]
    public async Task Pump_InterfaceWorkerThrows_ShouldRunItAgainAndReportFaulted()
    {
        // Arrange
        var worker = new EscapingWorker();
        var builder = KeyValueDatabaseEngine.CreateBuilder();
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

    private static KeyValueDatabaseEngineOptions Options(FaultInjectingJournalStorageStrategy strategy, TimeSpan? checkpoint = null, TimeSpan? writeBack = null) => new()
    {
        StorageStrategy = strategy,
        CheckpointInterval = checkpoint ?? TimeSpan.FromHours(1),
        PageWriteBackInterval = writeBack ?? TimeSpan.FromHours(1),
        MaintenanceInterval = TimeSpan.FromHours(1),
    };

    private static DatabaseEngineWorker WorkerOf(KeyValueDatabaseEngine engine, DatabaseEngineWorkerKind kind)
        => engine.Workers.OfType<DatabaseEngineWorker>().Single(worker => worker.Kind == kind);

    private static async Task PutAsync(KeyValueDatabaseInstance database, int first, int count)
    {
        await using var session = await database.CreateSessionAsync();
        for (int id = first; id < first + count; id++)
        {
            await database.PutAsync(session, Bytes($"k{id}"), Bytes(new string('x', 150)));
        }
    }

    private static async Task<List<TimeSpan>> TimedPutsAsync(KeyValueDatabaseInstance database, int count)
    {
        var latencies = new List<TimeSpan>();
        await using var session = await database.CreateSessionAsync();
        for (int id = 0; id < count; id++)
        {
            var watch = Stopwatch.StartNew();
            await database.PutAsync(session, Bytes($"grouped-{id}"), Bytes("grouped"));
            latencies.Add(watch.Elapsed);
        }

        return latencies;
    }

    private static async Task<int> CountAsync(KeyValueDatabaseInstance database)
    {
        await using var session = await database.CreateSessionAsync();
        int count = 0;
        await foreach (var _ in database.ScanAsync(session))
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
