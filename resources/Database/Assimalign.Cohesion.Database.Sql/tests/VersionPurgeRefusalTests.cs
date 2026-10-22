using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Sql.Internal;
using Assimalign.Cohesion.Database.Storage;
using Assimalign.Cohesion.Database.Storage.Units;
using Assimalign.Cohesion.Database.Storage.Tests.TestObjects;
using Assimalign.Cohesion.Database.Tests;

namespace Assimalign.Cohesion.Database.Sql.Tests;

[CollectionDefinition(nameof(VersionPurgeRefusalTests), DisableParallelization = true)]
[Collection(nameof(VersionPurgeRefusalTests))]
public sealed class VersionPurgeRefusalTests
{
    [Fact(DisplayName = "Cohesion Test [Sql] - Purge: refused undo reports once per episode without taking the database offline")]
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
        var worker = engine.Workers.OfType<SqlVersionPurgeWorker>().Single();
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
                        CheckpointPace.RunSettled(worker, token);
                        Interlocked.Increment(ref refusedPasses);
                        if (pass == 1)
                        {
                            for (int idle = 0; idle < 3; idle++) { CheckpointPace.RunSettled(worker, token); }
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
                CheckpointPace.RunSettled(worker, canceled.Token).ShouldBeTrue();
                worker.Fault.ShouldBeOfType<StorageTransactionException>();
                database.IsOffline.ShouldBeFalse();
            }
            clock.Advance(TimeSpan.FromHours(1));
            CheckpointPace.RunSettled(worker, token);
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

    [Theory(DisplayName = "Cohesion Test [Sql] - Purge: foreign storage fails immediately; changing lock owners remain busy")]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task RunIteration_RefusalKind_ShouldDistinguishForeignStorageFromProgress(bool foreign, bool walProgress)
    {
        // Arrange
        var clock = new ManualTimeProvider();
        await using var engine = CreateEngine(clock);
        var database = await engine.CreateDatabaseAsync("test", CancellationToken.None);
        var worker = engine.Workers.OfType<SqlVersionPurgeWorker>().Single();
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
            CheckpointPace.RunSettled(worker, CancellationToken.None);
        }

        // Assert
        owner.ShouldBe(passes);
        worker.FailureCount.ShouldBe(foreign || walProgress ? 1 : 0);
        if (foreign) { worker.Fault.ShouldBeOfType<StorageTransactionException>().Code.ShouldBe(StorageTransactionErrorCode.ForeignStorage); }
        else if (walProgress) { worker.Fault.ShouldBeOfType<StorageTransactionException>(); }
        else { worker.Fault.ShouldBeNull(); }
        worker.BeforeFullPass = null;
        clock.Advance(TimeSpan.FromHours(1));
        CheckpointPace.RunSettled(worker, CancellationToken.None);
        worker.Fault.ShouldBeNull();
        engine.OfflineDatabases.ShouldBeEmpty();
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - Checkpoint: foreign storage is reported; ordinary storage contention remains busy")]
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
        var worker = engine.Workers.OfType<SqlCheckpointWorker>().Single();
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
                CheckpointPace.RunSettled(worker, token);
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

    [Fact(DisplayName = "Cohesion Test [Sql] - Purge: closing on the last refused attempt cannot recreate the closed database's failure")]
    public async Task RunIteration_DatabaseClosesDuringTheRefusedPass_ShouldForgetItsRefusal()
    {
        // Arrange
        var clock = new ManualTimeProvider();
        await using var engine = CreateEngine(clock);
        var database = await engine.CreateDatabaseAsync("test", CancellationToken.None);
        var worker = engine.Workers.OfType<SqlVersionPurgeWorker>().Single();
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
            CheckpointPace.RunSettled(worker, CancellationToken.None).ShouldBeTrue();
        }

        // Assert
        attempts.ShouldBe(3);
        database.IsClosed.ShouldBeTrue();
        worker.FailureCount.ShouldBe(0);
        worker.Fault.ShouldBeNull();
        engine.HasFailingWorker(database.Name).ShouldBeFalse();
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - Purge: a successful reclamation batch clears its refused-work report between full passes")]
    public async Task RunIteration_ReclamationRefusalClears_ShouldRecoverBeforeTheNextFullPass()
    {
        // Arrange: only the short reclamation polling runs; the full-pass clock stays still.
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var token = timeout.Token;
        var clock = new ManualTimeProvider();
        await using var engine = CreateEngine(clock);
        var database = await engine.CreateDatabaseAsync("test", token);
        await using var session = await database.CreateSessionAsync(token);
        await SetupAsync(session, token);
        await PutAsync(session, 1, token);
        var worker = engine.Workers.OfType<SqlVersionPurgeWorker>().Single();
        int fullPasses = 0;
        int refused = 0;
        worker.BeforeFullPass = _ => Interlocked.Increment(ref fullPasses);
        database.TableReclamation.BeforeBatch = (_, _) =>
        {
            Interlocked.Increment(ref refused);
            throw new StorageTransactionException("Injected unchanged reclamation refusal.");
        };
        try
        {
            // Act: three attempts spanning one backoff, followed by one successful batch.
            await session.ExecuteAsync("DROP TABLE items", cancellationToken: token);
            for (int pass = 0; pass < 3; pass++)
            {
                CheckpointPace.RunSettled(worker, token);
                clock.Advance(DatabaseEngineWorker.FailureBackoff / 2);
            }
            Volatile.Read(ref refused).ShouldBeGreaterThanOrEqualTo(3);
            worker.Fault.ShouldBeOfType<StorageTransactionException>();
            database.TableReclamation.BeforeBatch = null;
            CheckpointPace.RunSettled(worker, token);

            // Assert: a successful bounded batch finishes the refused work without an hourly pass.
            database.TableReclamation.HasWork.ShouldBeFalse();
            worker.Fault.ShouldBeNull();
            worker.FailureCount.ShouldBe(1);
            Volatile.Read(ref fullPasses).ShouldBe(0);
            engine.HasFailingWorker(database.Name).ShouldBeFalse();
        }
        finally
        {
            database.TableReclamation.BeforeBatch = null;
        }
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - Purge: a busy full pass retries after the backoff")]
    public async Task RunIteration_BusyFullPass_ShouldRetryAfterBackoff()
    {
        // Arrange
        var clock = new ManualTimeProvider();
        await using var engine = CreateEngine(clock);
        await engine.CreateDatabaseAsync("test", CancellationToken.None);
        var worker = engine.Workers.OfType<SqlVersionPurgeWorker>().Single();
        int attempts = 0;
        worker.BeforeFullPass = _ =>
        {
            attempts++;
            throw new StorageTransactionException("Page 1 is locked by transaction 9.");
        };

        // Act
        clock.Advance(TimeSpan.FromHours(1));
        CheckpointPace.RunSettled(worker, CancellationToken.None);
        clock.Advance(DatabaseEngineWorker.FailureBackoff / 2);
        CheckpointPace.RunSettled(worker, CancellationToken.None);
        attempts.ShouldBe(1);
        clock.Advance(DatabaseEngineWorker.FailureBackoff / 2);
        CheckpointPace.RunSettled(worker, CancellationToken.None);

        // Assert
        attempts.ShouldBe(2);
        worker.BeforeFullPass = null;
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - Purge: three short refused undo retries do not report an ordinary bracket")]
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
        var worker = new SqlVersionPurgeWorker(engine);
        await using var lifetime = CheckpointPace.Own(worker);
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
                    CheckpointPace.RunSettled(worker, token);
                }
                worker.FailureCount.ShouldBe(0);
                clock.Advance(TimeSpan.FromMilliseconds(800));
                CheckpointPace.RunSettled(worker, token);
            }, token);

            // Assert
            worker.FailureCount.ShouldBe(1);
            engine.HasFailingWorker(database.Name).ShouldBeFalse();
        }
        clock.Advance(TimeSpan.FromSeconds(2));
        CheckpointPace.RunSettled(worker, token);
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - Purge: unrelated committed page writes cannot suppress a blocked committed tombstone")]
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
        await session.ExecuteAsync("DELETE FROM items", cancellationToken: token);
        database.Coordinator.VersionStore.TrackedVersionCount.ShouldBeGreaterThan(0);
        database.Coordinator.VersionStore.PendingAbortedPurges.ShouldBeEmpty();
        PageId traffic;
        using (var allocation = database.DataStorage.BeginTransaction())
        {
            using (var page = database.DataStorage.AllocatePageForWrite(allocation, PageType.Index)) { traffic = page.Id; }
            allocation.Commit();
        }
        var worker = engine.Workers.OfType<SqlVersionPurgeWorker>().Single();
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
                CheckpointPace.RunSettled(worker, token);
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
        CheckpointPace.RunSettled(worker, token);
        worker.Fault.ShouldBeNull();
        database.Coordinator.VersionStore.TrackedVersionCount.ShouldBe(0);
    }

    [Theory(DisplayName = "Cohesion Test [Database.Sql] - Purge lanes: full work becoming due during a detached pass remains owed")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RunIteration_FullPassBecomesDueWhileLaneRuns_ShouldCollectThenRunTheOwedPass(bool failFirst)
    {
        // Arrange: run through the pending-epoch, detached failure and recovery races repeatedly.
        var duration = System.Diagnostics.Stopwatch.StartNew();
        int rounds = 0;
        int detached = 0;
        int failuresCollected = 0;
        int fullPasses = 0;
        do
        {
            var clock = new ManualTimeProvider();
            await using var engine = CreateEngine(clock);
            var database = await engine.CreateDatabaseAsync("test", CancellationToken.None);
            var worker = engine.Workers.OfType<SqlVersionPurgeWorker>().Single();
            using var entered = new ManualResetEventSlim();
            using var release = new ManualResetEventSlim();
            int calls = 0;
            int active = 0;
            int overlap = 0;
            worker.BeforeFullPass = _ =>
            {
                if (Interlocked.Increment(ref active) > 1) { Interlocked.Increment(ref overlap); }
                try
                {
                    if (Interlocked.Increment(ref calls) == 1)
                    {
                        entered.Set();
                        release.Wait();
                        if (failFirst) { throw new System.IO.IOException("detached purge failed"); }
                    }
                }
                finally { Interlocked.Decrement(ref active); }
            };
            try
            {
                // Act: the second interval becomes due before the first lane can finish.
                clock.Advance(TimeSpan.FromHours(1));
                worker.RunIteration(CancellationToken.None);
                entered.Wait(TimeSpan.FromSeconds(30)).ShouldBeTrue();
                worker.WaitForLanes(TimeSpan.Zero).ShouldBeFalse();
                detached++;
                clock.Advance(TimeSpan.FromHours(1));
                worker.RunIteration(CancellationToken.None);
                calls.ShouldBe(1);
                worker.FailureCount.ShouldBe(0, "a lane cannot report outside its collecting pass");
            }
            finally { release.Set(); }
            var settled = System.Diagnostics.Stopwatch.StartNew();
            while ((Volatile.Read(ref calls) < 2 || worker.Fault is not null)
                && settled.Elapsed < TimeSpan.FromSeconds(30)) { await Task.Delay(5); }
            worker.WaitForLanes(TimeSpan.FromSeconds(30)).ShouldBeTrue();

            // Assert: the real pump collected and ran the owed purge, without another explicit
            // iteration or clock advance. A missing wake would leave it asleep for an hour.
            calls.ShouldBe(2);
            overlap.ShouldBe(0);
            worker.FailureCount.ShouldBe(failFirst ? 1 : 0);
            worker.Fault.ShouldBeNull();
            if (failFirst) { failuresCollected++; }
            fullPasses += calls;
            rounds++;
        }
        while ((duration.Elapsed < TimeSpan.FromMilliseconds(250) || rounds < 6)
            && duration.Elapsed < TimeSpan.FromSeconds(30));
        rounds.ShouldBeGreaterThanOrEqualTo(6);
        detached.ShouldBe(rounds);
        fullPasses.ShouldBe(rounds * 2);
        failuresCollected.ShouldBe(failFirst ? rounds : 0);
    }

    private static SqlDatabaseEngine CreateEngine(ManualTimeProvider clock, bool checkpoint = false)
        => SqlDatabaseEngine.Create("purge-refusal", new SqlDatabaseEngineOptions
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

    private static async Task SetupAsync(SqlDatabaseSession session, CancellationToken token)
    {
        await session.ExecuteAsync("CREATE TABLE items (id INT)", cancellationToken: token);
    }

    private static async Task PutAsync(SqlDatabaseSession session, int id, CancellationToken token)
    {
        await session.ExecuteAsync($"INSERT INTO items VALUES ({id})", cancellationToken: token);
    }
}
