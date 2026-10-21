using System;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.KeyValuePair.Internal;
using Assimalign.Cohesion.Database.Storage;
using Assimalign.Cohesion.Database.Storage.Tests.TestObjects;
using Assimalign.Cohesion.Database.Tests;

namespace Assimalign.Cohesion.Database.KeyValuePair.Tests;

[CollectionDefinition(nameof(VersionPurgeRefusalTests), DisableParallelization = true)]
[Collection(nameof(VersionPurgeRefusalTests))]
public sealed class VersionPurgeRefusalTests
{
    [Fact(DisplayName = "Cohesion Test [KeyValuePair] - Purge: refused undo reports once per episode without taking the database offline")]
    public async Task RunIteration_UndoRemainsRefused_ShouldReportOnceAndRecoverForEachEpisode()
    {
        // Arrange: timers wait an hour. These explicit full passes retry the undo on another thread.
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var token = timeout.Token;
        var clock = new ManualTimeProvider();
        await using var engine = CreateEngine(clock);
        var database = await engine.CreateDatabaseAsync("test", token);
        var healthy = await engine.CreateDatabaseAsync("healthy", token);
        await using var session = await database.CreateSessionAsync(token);
        await SetupAsync(session, token);
        var worker = engine.Workers.OfType<KeyValueVersionPurgeWorker>().Single();
        int healthyPasses = 0;
        worker.BeforeFullPass = current =>
        {
            if (ReferenceEquals(current, healthy)) { Interlocked.Increment(ref healthyPasses); }
        };
        long failuresBefore = worker.FailureCount;
        int episodes = 0;
        int refusedPasses = 0;
        var watch = Stopwatch.StartNew();

        // Act: run for a minimum, then until counters prove repeated refusal/recovery, with a ceiling.
        do
        {
            await using var transaction = await session.BeginTransactionAsync(cancellationToken: token);
            await PutAsync(session, episodes, token);
            using (var holder = PageWriteLockHolder.LockEveryPage(database.DataStorage))
            {
                holder.Pages.ShouldBeGreaterThan(0);
                await transaction.RollbackAsync(token);
                database.Coordinator.VersionStore.PendingAbortedPurges.ShouldNotBeEmpty();
                await Task.Run(() =>
                {
                    for (int pass = 0; pass < 12; pass++)
                    {
                        clock.Advance(TimeSpan.FromHours(1));
                        worker.RunIteration(token);
                        Interlocked.Increment(ref refusedPasses);
                        if (pass == 1)
                        {
                            for (int idle = 0; idle < 3; idle++) { worker.RunIteration(token); }
                            worker.FailureCount.ShouldBe(failuresBefore + episodes);
                        }
                    }
                }, token);
                worker.FailureCount.ShouldBe(failuresBefore + episodes + 1);
                worker.Fault.ShouldBeOfType<StorageTransactionException>();
                engine.HasFailingWorker(database.Name).ShouldBeTrue();
                engine.HasFailingWorker(healthy.Name).ShouldBeFalse();
                using var canceled = new CancellationTokenSource();
                canceled.Cancel();
                worker.RunIteration(canceled.Token).ShouldBeTrue();
                worker.Fault.ShouldBeOfType<StorageTransactionException>();
                database.IsOffline.ShouldBeFalse();
            }
            clock.Advance(TimeSpan.FromHours(1));
            worker.RunIteration(token);
            database.Coordinator.VersionStore.PendingAbortedPurges.ShouldBeEmpty();
            worker.Fault.ShouldBeNull();
            engine.State.ShouldBe(EngineState.Running);
            database.Coordinator.PairedTransactionCount.ShouldBe(0);
            database.Coordinator.Checkpoint();
            episodes++;
        }
        while ((watch.Elapsed < TimeSpan.FromMilliseconds(250) || episodes < 3) && watch.Elapsed < TimeSpan.FromSeconds(10));

        // Assert: no offline work was queued although the clock crossed the give-up window.
        await Task.Delay(20, token);
        episodes.ShouldBeGreaterThanOrEqualTo(3);
        refusedPasses.ShouldBe(12 * episodes);
        healthyPasses.ShouldBeGreaterThanOrEqualTo(refusedPasses);
        worker.FailureCount.ShouldBe(failuresBefore + episodes);
        engine.OfflineDatabases.ShouldBeEmpty();
    }

    [Theory(DisplayName = "Cohesion Test [KeyValuePair] - Purge: foreign storage fails immediately; changing lock owners remain busy")]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task RunIteration_RefusalKind_ShouldDistinguishForeignStorageFromProgress(bool foreign, bool walProgress)
    {
        // Arrange
        var clock = new ManualTimeProvider();
        await using var engine = CreateEngine(clock);
        var database = await engine.CreateDatabaseAsync("test", CancellationToken.None);
        var worker = engine.Workers.OfType<KeyValueVersionPurgeWorker>().Single();
        int owner = 0;
        worker.BeforeFullPass = _ =>
        {
            owner++;
            throw new StorageTransactionException(
                foreign ? StorageTransactionErrorCode.ForeignStorage : StorageTransactionErrorCode.Unspecified,
                $"Page 1 is locked by transaction {(walProgress ? 1 : owner)}.");
        };

        // Act
        int passes = foreign ? 1 : 12;
        for (int pass = 0; pass < passes; pass++)
        {
            if (walProgress) { database.DataStorage.WriteAheadJournal.AppendOperation(0, new byte[1]); }
            clock.Advance(TimeSpan.FromHours(1));
            worker.RunIteration(CancellationToken.None);
        }

        // Assert
        owner.ShouldBe(passes);
        worker.FailureCount.ShouldBe(foreign ? 1 : 0);
        if (foreign) { worker.Fault.ShouldBeOfType<StorageTransactionException>().Code.ShouldBe(StorageTransactionErrorCode.ForeignStorage); }
        else { worker.Fault.ShouldBeNull(); }
        worker.BeforeFullPass = null;
        clock.Advance(TimeSpan.FromHours(1));
        worker.RunIteration(CancellationToken.None);
        worker.Fault.ShouldBeNull();
        engine.OfflineDatabases.ShouldBeEmpty();
    }

    [Theory(DisplayName = "Cohesion Test [KeyValuePair] - Checkpoint: foreign storage is reported; ordinary storage contention remains busy")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RunIteration_CheckpointRefusal_ShouldReportOnlyForeignStorage(bool foreign)
    {
        // Arrange
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var token = timeout.Token;
        var clock = new ManualTimeProvider();
        await using var engine = CreateEngine(clock, checkpoint: true);
        var database = await engine.CreateDatabaseAsync("test", token);
        await using var session = await database.CreateSessionAsync(token);
        await SetupAsync(session, token);
        var worker = engine.Workers.OfType<KeyValueCheckpointWorker>().Single();
        int attempts = 0;
        worker.BeforeCheckpoint = _ =>
        {
            Interlocked.Increment(ref attempts);
            throw new StorageTransactionException(
                foreign ? StorageTransactionErrorCode.ForeignStorage : StorageTransactionErrorCode.Unspecified,
                "Injected checkpoint refusal.");
        };
        try
        {
            // Act: run until the first failure is collected, or a second busy attempt proves the
            // first was collected. Keep a minimum and a ceiling, and assert those counters.
            await PutAsync(session, 0, token);
            var watch = Stopwatch.StartNew();
            do
            {
                worker.RunIteration(token);
                await Task.Delay(5, token);
            }
            while ((watch.Elapsed < TimeSpan.FromMilliseconds(100)
                || (foreign ? worker.FailureCount == 0 : Volatile.Read(ref attempts) < 2))
                && watch.Elapsed < TimeSpan.FromSeconds(10));

            // Assert
            Volatile.Read(ref attempts).ShouldBeGreaterThanOrEqualTo(foreign ? 1 : 2);
            if (foreign)
            {
                worker.FailureCount.ShouldBeGreaterThanOrEqualTo(1);
                worker.Fault.ShouldBeOfType<StorageTransactionException>().Code.ShouldBe(StorageTransactionErrorCode.ForeignStorage);
            }
            else
            {
                worker.FailureCount.ShouldBe(0);
                worker.Fault.ShouldBeNull();
            }
        }
        finally
        {
            worker.BeforeCheckpoint = null;
        }
    }

    [Fact(DisplayName = "Cohesion Test [KeyValuePair] - Purge: closing on the last refused attempt cannot recreate the closed database's failure")]
    public async Task RunIteration_DatabaseClosesDuringTheRefusedPass_ShouldForgetItsRefusal()
    {
        // Arrange
        var clock = new ManualTimeProvider();
        await using var engine = CreateEngine(clock);
        var database = await engine.CreateDatabaseAsync("test", CancellationToken.None);
        var worker = engine.Workers.OfType<KeyValueVersionPurgeWorker>().Single();
        int attempts = 0;
        worker.BeforeFullPass = current =>
        {
            if (++attempts == 3) { current.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
            throw new StorageTransactionException("Injected unchanged page refusal.");
        };

        // Act
        for (int pass = 0; pass < 3; pass++)
        {
            clock.Advance(TimeSpan.FromHours(1));
            worker.RunIteration(CancellationToken.None).ShouldBeTrue();
        }

        // Assert
        attempts.ShouldBe(3);
        database.IsClosed.ShouldBeTrue();
        worker.FailureCount.ShouldBe(0);
        worker.Fault.ShouldBeNull();
        engine.HasFailingWorker(database.Name).ShouldBeFalse();
    }

    private static KeyValueDatabaseEngine CreateEngine(ManualTimeProvider clock, bool checkpoint = false)
        => KeyValueDatabaseEngine.Create("purge-refusal", new KeyValueDatabaseEngineOptions
        {
            MaintenanceInterval = TimeSpan.FromHours(1),
            CheckpointInterval = TimeSpan.FromHours(1),
            PageWriteBackInterval = TimeSpan.FromHours(1),
            DeferredUndoRetryDelay = TimeSpan.FromHours(1),
            WorkerFailureWindow = TimeSpan.FromMilliseconds(1),
            WorkerFailureMinimumPasses = checkpoint ? int.MaxValue : 1,
            CheckpointJournalSize = checkpoint ? 1 : 0,
            TimeProvider = clock,
        });

    private static async Task SetupAsync(KeyValueDatabaseSession session, CancellationToken token)
    {
        await Task.CompletedTask;
    }

    private static async Task PutAsync(KeyValueDatabaseSession session, int id, CancellationToken token)
    {
        await session.Database.PutAsync(session, BitConverter.GetBytes(id), new byte[128], cancellationToken: token);
    }
}
