using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Graph.Internal;
using Assimalign.Cohesion.Database.Storage;
using Assimalign.Cohesion.Database.Storage.Units;
using Assimalign.Cohesion.Database.Storage.Tests.TestObjects;
using Assimalign.Cohesion.Database.Tests;

namespace Assimalign.Cohesion.Database.Graph.Tests;

[CollectionDefinition(nameof(VersionPurgeRefusalTests), DisableParallelization = true)]
[Collection(nameof(VersionPurgeRefusalTests))]
public sealed class VersionPurgeRefusalTests
{
    [Fact(DisplayName = "Cohesion Test [Graph] - Purge: refused undo reports once per episode without taking the database offline")]
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
        var worker = engine.Workers.OfType<GraphVersionPurgeWorker>().Single();
        int healthyPasses = 0;
        worker.BeforeFullPass = current =>
        {
            if (ReferenceEquals(current, healthy)) { Interlocked.Increment(ref healthyPasses); }
        };
        long failuresBefore = worker.FailureCount;
        int episodes = 0;
        int refusedPasses = 0;

        // Act: three complete refusal/recovery episodes, each with twelve refused attempts.
        for (int episode = 0; episode < 3; episode++)
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
                engine.HasFailingWorker(database.Name).ShouldBeFalse();
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

        // Assert: no offline work was queued although the clock crossed the give-up window.
        episodes.ShouldBe(3);
        refusedPasses.ShouldBe(12 * episodes);
        healthyPasses.ShouldBeGreaterThanOrEqualTo(refusedPasses);
        worker.FailureCount.ShouldBe(failuresBefore + episodes);
        engine.OfflineDatabases.ShouldBeEmpty();
    }

    [Theory(DisplayName = "Cohesion Test [Graph] - Purge: foreign storage fails immediately; changing lock owners remain busy")]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task RunIteration_RefusalKind_ShouldDistinguishForeignStorageFromProgress(bool foreign, bool walProgress)
    {
        // Arrange
        var clock = new ManualTimeProvider();
        await using var engine = CreateEngine(clock);
        var database = await engine.CreateDatabaseAsync("test", CancellationToken.None);
        var worker = engine.Workers.OfType<GraphVersionPurgeWorker>().Single();
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
        worker.FailureCount.ShouldBe(foreign || walProgress ? 1 : 0);
        if (foreign) { worker.Fault.ShouldBeOfType<StorageTransactionException>().Code.ShouldBe(StorageTransactionErrorCode.ForeignStorage); }
        else if (walProgress) { worker.Fault.ShouldBeOfType<StorageTransactionException>(); }
        else { worker.Fault.ShouldBeNull(); }
        worker.BeforeFullPass = null;
        clock.Advance(TimeSpan.FromHours(1));
        worker.RunIteration(CancellationToken.None);
        worker.Fault.ShouldBeNull();
        engine.OfflineDatabases.ShouldBeEmpty();
    }

    [Theory(DisplayName = "Cohesion Test [Graph] - Checkpoint: foreign storage is reported; ordinary storage contention remains busy")]
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
        var worker = engine.Workers.OfType<GraphCheckpointWorker>().Single();
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
            // Act: wait for each dispatched lane, then collect it with the next fixed pass.
            await PutAsync(session, 0, token);
            for (int pass = 0; pass < 3; pass++)
            {
                worker.RunIteration(token);
                worker.WaitForCheckpoints(TimeSpan.FromSeconds(30)).ShouldBeTrue();
            }

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

    [Fact(DisplayName = "Cohesion Test [Graph] - Purge: closing on the last refused attempt cannot recreate the closed database's failure")]
    public async Task RunIteration_DatabaseClosesDuringTheRefusedPass_ShouldForgetItsRefusal()
    {
        // Arrange
        var clock = new ManualTimeProvider();
        await using var engine = CreateEngine(clock);
        var database = await engine.CreateDatabaseAsync("test", CancellationToken.None);
        var worker = engine.Workers.OfType<GraphVersionPurgeWorker>().Single();
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

    [Fact(DisplayName = "Cohesion Test [Graph] - Purge: a busy full pass retries after the backoff")]
    public async Task RunIteration_BusyFullPass_ShouldRetryAfterBackoff()
    {
        // Arrange
        var clock = new ManualTimeProvider();
        await using var engine = CreateEngine(clock);
        await engine.CreateDatabaseAsync("test", CancellationToken.None);
        var worker = engine.Workers.OfType<GraphVersionPurgeWorker>().Single();
        int attempts = 0;
        worker.BeforeFullPass = _ =>
        {
            attempts++;
            throw new StorageTransactionException("Page 1 is locked by transaction 9.");
        };

        // Act
        clock.Advance(TimeSpan.FromHours(1));
        worker.RunIteration(CancellationToken.None);
        clock.Advance(DatabaseEngineWorker.FailureBackoff / 2);
        worker.RunIteration(CancellationToken.None);
        attempts.ShouldBe(1);
        clock.Advance(DatabaseEngineWorker.FailureBackoff / 2);
        worker.RunIteration(CancellationToken.None);

        // Assert
        attempts.ShouldBe(2);
        worker.BeforeFullPass = null;
    }

    [Fact(DisplayName = "Cohesion Test [Graph] - Purge: three short refused undo retries do not report an ordinary bracket")]
    public async Task RunIteration_ShortRefusal_ShouldWaitForMinimumElapsedTime()
    {
        // Arrange
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var token = timeout.Token;
        var clock = new ManualTimeProvider();
        await using var engine = CreateEngine(clock);
        var database = await engine.CreateDatabaseAsync("test", token);
        await using var session = await database.CreateSessionAsync(token);
        await SetupAsync(session, token);
        var worker = new GraphVersionPurgeWorker(engine);
        worker.BeforeDeferredUndoRetry = current => current.Coordinator.RunVersionPurgePass(token);
        await using var transaction = await session.BeginTransactionAsync(cancellationToken: token);
        await PutAsync(session, 0, token);
        using (var holder = PageWriteLockHolder.LockEveryPage(database.DataStorage))
        {
            await transaction.RollbackAsync(token);

            // Act: force real undo retries at 100, 300 and 700 ms, spanning only 600 ms.
            // The local worker has no pump; the kernel's system-clock due time cannot race it.
            await Task.Run(() =>
            {
                foreach (int milliseconds in new[] { 100, 200, 400 })
                {
                    clock.Advance(TimeSpan.FromMilliseconds(milliseconds));
                    worker.RunIteration(token);
                }
                worker.FailureCount.ShouldBe(0);
                clock.Advance(TimeSpan.FromMilliseconds(800));
                worker.RunIteration(token);
            }, token);

            // Assert
            worker.FailureCount.ShouldBe(1);
            engine.HasFailingWorker(database.Name).ShouldBeFalse();
        }
        clock.Advance(TimeSpan.FromSeconds(2));
        worker.RunIteration(token);
    }

    [Fact(DisplayName = "Cohesion Test [Graph] - Purge: unrelated committed page writes cannot suppress a blocked committed tombstone")]
    public async Task RunIteration_CommittedPurgeWithUnrelatedTraffic_ShouldReportTheSameBlocker()
    {
        // Arrange: commit a deletion, then retain every existing page's write lock except the
        // separate traffic page. There is no deferred undo or changing logical writer.
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var token = timeout.Token;
        var clock = new ManualTimeProvider();
        await using var engine = CreateEngine(clock);
        var database = await engine.CreateDatabaseAsync("test", token);
        await using var session = await database.CreateSessionAsync(token);
        await SetupAsync(session, token);
        await PutAsync(session, 0, token);
        await session.ExecuteAsync("MATCH (n:Pending) DELETE n", cancellationToken: token);
        database.Coordinator.VersionStore.TrackedVersionCount.ShouldBeGreaterThan(0);
        database.Coordinator.VersionStore.PendingAbortedPurges.ShouldBeEmpty();
        PageId traffic;
        using (var allocation = database.DataStorage.BeginTransaction())
        {
            using (var page = database.DataStorage.AllocatePageForWrite(allocation, PageType.Index)) { traffic = page.Id; }
            allocation.Commit();
        }
        var worker = engine.Workers.OfType<GraphVersionPurgeWorker>().Single();
        using (var blocker = database.DataStorage.BeginTransaction())
        {
            for (long page = 1; page < database.DataStorage.PageManager.PageCount; page++)
            {
                if ((PageId)page == traffic) { continue; }
                using var locked = database.DataStorage.OpenPageForWrite(blocker, (PageId)page);
            }

            // Act: twelve refusals with real, committed writes elsewhere in the same storage.
            for (int pass = 0; pass < 12; pass++)
            {
                long beforeTraffic = database.DataStorage.WriteAheadJournal.LastLsn;
                using (var write = database.DataStorage.BeginTransaction())
                {
                    using (var page = database.DataStorage.OpenPageForWrite(write, traffic)) { page.Page.AsBodySpan()[0] = (byte)(pass + 1); }
                    write.Commit();
                }
                database.DataStorage.WriteAheadJournal.LastLsn.ShouldBeGreaterThan(beforeTraffic);
                clock.Advance(TimeSpan.FromHours(1));
                worker.RunIteration(token);
            }

            // Assert: the same page and owning bracket are one bounded refusal episode.
            worker.FailureCount.ShouldBe(1);
            worker.Fault.ShouldBeOfType<StorageTransactionException>();
            database.Coordinator.VersionStore.TrackedVersionCount.ShouldBeGreaterThan(0);
            engine.HasFailingWorker(database.Name).ShouldBeFalse();
            database.IsOffline.ShouldBeFalse();
            blocker.Rollback();
        }
        clock.Advance(TimeSpan.FromHours(1));
        worker.RunIteration(token);
        worker.Fault.ShouldBeNull();
        database.Coordinator.VersionStore.TrackedVersionCount.ShouldBe(0);
    }

    private static GraphDatabaseEngine CreateEngine(ManualTimeProvider clock, bool checkpoint = false)
        => GraphDatabaseEngine.Create("purge-refusal", new GraphDatabaseEngineOptions
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

    private static async Task SetupAsync(GraphDatabaseSession session, CancellationToken token)
    {
        await Task.CompletedTask;
    }

    private static async Task PutAsync(GraphDatabaseSession session, int id, CancellationToken token)
    {
        await session.ExecuteAsync("INSERT (:Pending)", cancellationToken: token);
    }
}
