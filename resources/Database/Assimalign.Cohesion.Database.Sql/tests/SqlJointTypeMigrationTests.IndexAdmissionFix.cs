using System;
using System.Collections.Generic;
using System.Diagnostics.Tracing;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Sql.Internal;
using Assimalign.Cohesion.Database.Sql.Tests.TestObjects;
using Assimalign.Cohesion.Database.Sql.Schema;
using Assimalign.Cohesion.Database.Transactions;
using Assimalign.Cohesion.Testing;

namespace Assimalign.Cohesion.Database.Sql.Tests;

public sealed partial class SqlJointTypeMigrationTests
{
    [Fact(DisplayName = "Cohesion Test [Sql] - Ordinary index build decodes each source row once (#1808)")]
    public async Task ExecuteAsync_OrdinaryIndexBuild_ShouldScanHeapOnce()
    {
        // Arrange.
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        await using var engine = SqlDatabaseEngine.Create("one-index-scan", new());
        var database = await engine.CreateDatabaseAsync("test", guard.Token);
        await using var session = await database.CreateSessionAsync(guard.Token);
        await session.ExecuteAsync("CREATE TABLE items(id INT)", cancellationToken: guard.Token);
        for (int row = 0; row < 257; row++)
        { await session.ExecuteAsync($"INSERT INTO items VALUES({row})", cancellationToken: guard.Token); }
        int decoded = 0;
        SqlPlanExecutor.RowDecoded = _ => decoded++;

        // Act.
        try { await session.ExecuteAsync("CREATE INDEX ix ON items(id)", cancellationToken: guard.Token); }
        finally { SqlPlanExecutor.RowDecoded = null; }

        // Assert.
        decoded.ShouldBe(257);
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - Catalog-only DROP hands a partially reclaimed owner to retirement instead of pruning its freed root (#1808)")]
    public async Task PruneAsync_CatalogOnlyDrop_ShouldSkipRetiringOwner()
    {
        // Arrange.
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        await using var engine = SqlDatabaseEngine.Create("catalog-drop-prune", new() { MaintenanceInterval = TimeSpan.FromHours(1) });
        var database = await engine.CreateDatabaseAsync("typed", guard.Token);
        await WithPausedMaintenanceAsync(engine, guard.Token, async () =>
        {
            await using var writer = await database.CreateSessionAsync(guard.Token);
            await writer.ExecuteAsync("CREATE TABLE items(v VARCHAR(400))", cancellationToken: guard.Token);
            SeedRaw(database, [new string('x', 300)], 2048);
            await writer.ExecuteAsync("CREATE INDEX kept ON items(v)", cancellationToken: guard.Token);
            await writer.ExecuteAsync("CREATE INDEX lost ON items(v)", cancellationToken: guard.Token);
            ulong owner = database.IndexManager.PageOwner(database.IndexManager.ExportRegistrations().Single(item => item.Definition.Name == "lost"));
            int before = database.DataStorage.GetOwnerIndexPageCount(owner);
            before.ShouldBeGreaterThan(64);

            // Act: free a first batch containing the root, with the old manager still attached.
            await database.Catalog.DropIndexAsync(Table(database).ObjectId, "lost", [], guard.Token);
            database.TableReclamation.RunBatch(guard.Token);
            database.DataStorage.GetOwnerIndexPageCount(owner).ShouldBe(before - SqlTableReclamation.BatchSize);
            using (var lease = TransactionCheckpointInterlock.ForStorage(database.DataStorage).Enter("PruneAfterCatalogDrop", cancellationToken: guard.Token))
            {
                await database.IndexManager.PruneAsync(database.Coordinator.PruneBound, guard.Token);
                await database.IndexManager.PruneAsync(database.Coordinator.PruneBound, guard.Token);
            }

            // Assert.
            database.IsOffline.ShouldBeFalse();
            (await RowsAsync(writer, "SELECT COUNT(*) FROM items", guard.Token)).Single()[0].ShouldBe(2048L);
        });
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - An index owner yields to a due or pending checkpoint (#1808)")]
    [InlineData(false)] [InlineData(true)]
    public async Task RunBatch_IndexOwnerCheckpoint_ShouldYieldThenReclaim(bool pending)
    {
        // Arrange.
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        await using var engine = SqlDatabaseEngine.Create("index-yield", new()
        { CheckpointJournalSize = 0, MaintenanceInterval = TimeSpan.FromHours(1), CheckpointInterval = TimeSpan.FromHours(1) });
        var database = await engine.CreateDatabaseAsync("typed", guard.Token);
        await WithPausedMaintenanceAsync(engine, guard.Token, async () =>
        {
            await using var writer = await database.CreateSessionAsync(guard.Token);
            await writer.ExecuteAsync("CREATE TABLE items(v INT)", cancellationToken: guard.Token);
            SeedRaw(database, [1], 130);
            await writer.ExecuteAsync("CREATE INDEX ix ON items(v)", cancellationToken: guard.Token);
            ulong owner = database.IndexManager.PageOwner(database.IndexManager.ExportRegistrations().Single());
            await writer.ExecuteAsync("DROP INDEX ix ON items", cancellationToken: guard.Token);
            var pages = database.DataStorage.GetOwnerIndexPages(owner).ToArray();
            using var events = new EventSourceRecorder(SqlDatabaseEventSource.Log, EventLevel.Verbose);
            using (var holder = database.DataStorage.BeginTransaction("HoldIndexCheckpoint"))
            {
                database.DataStorage.CheckpointJournalSize = pending ? 0 : 1;
                if (pending) { engine.CheckpointNeededSignal.Set(); }

                // Act.
                database.TableReclamation.RunBatch(guard.Token);

                // Assert.
                database.DataStorage.GetOwnerIndexPages(owner).ShouldBe(pages);
                events.Events.ShouldContain(item => item.EventId == 27 && Equals(item.Payload![2], owner));
            }
            engine.CheckpointNeededSignal.Reset(); database.DataStorage.CheckpointJournalSize = 0;
            database.Coordinator.Checkpoint(guard.Token);
            await DrainReclamationAsync(database, guard.Token);
            database.DataStorage.GetOwnerIndexPages(owner).ShouldBeEmpty();
            database.Catalog.GetPendingTableReclamations().ShouldBeEmpty();
        });
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - New-table owners wait for catalog admission before data apply (#1808)")]
    public async Task ApplySchemaAsync_NewTableCatalogPause_ShouldReserveBothTreesOutsideDataGate()
    {
        // Arrange.
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        var strategy = new CrashCaptureSqlStorageStrategy();
        await using var engine = await Builder(strategy).BuildAsync(guard.Token);
        var database = await engine.CreateDatabaseAsync("typed", guard.Token);
        SqlCompiledSchema Declare(bool target) => SqlSchema.Create("typed", schema =>
        {
            schema.Table<OtherRow>("items", table => { table.Key(row => row.Id); if (target) { table.Index(row => row.Id); } });
            if (target) { schema.Table<DeferredCreated>("created", table => { table.Key(row => row.Id); table.Index(row => row.Id); }); }
        }).Compile();
        await database.ApplySchemaAsync(Declare(false), guard.Token);
        await using (var seed = await database.CreateSessionAsync(guard.Token))
        { await seed.ExecuteAsync("INSERT INTO items VALUES(1)", cancellationToken: guard.Token); }
        strategy.DurableStorageChanged = catalog =>
        { if (catalog) { database.DataStorage.HasActiveTransactions.ShouldBeFalse("catalog durability must precede the data bracket"); } };
        await WithPausedMaintenanceAsync(engine, guard.Token, async () =>
        {
            using var armed = new ManualResetEventSlim();
            Assimalign.Cohesion.Database.Storage.StorageTransaction? outside = null;
            var catalogGate = TransactionCheckpointInterlock.ForStorage(database.CatalogStorage);
            var stages = new System.Collections.Concurrent.ConcurrentQueue<string>();
            database.SchemaProvisioner.JointStage = stage =>
            {
                stages.Enqueue(stage);
                if (stage != "OwnersDurable") { return; }
                database.CatalogStorage.CheckpointJournalSize = 1; catalogGate.JournalSizeLimit = 2;
                outside = database.CatalogStorage.BeginTransaction("CatalogPauseTest");
                catalogGate.ObserveAdmissionPause().ShouldBeTrue(); armed.Set();
            };

            // Act: data admission remains available while catalog admission waits.
            var migration = Task.Run(async () => await database.ApplySchemaAsync(Declare(true), guard.Token), guard.Token);
            try
            {
                var paused = Task.Run(() => armed.Wait(guard.Token), guard.Token);
                await Task.WhenAny(paused, migration).WaitAsync(guard.Token);
                if (migration.IsCompleted) { await migration; }
                await paused;
                await Task.Run(() =>
                { using var lease = TransactionCheckpointInterlock.ForStorage(database.DataStorage).Enter("OtherTable", cancellationToken: guard.Token); }, guard.Token).WaitAsync(guard.Token);
                outside.ShouldNotBeNull().Rollback(); outside = null;
                database.CatalogStorage.CheckpointJournalSize = 0; catalogGate.JournalSizeLimit = 0;
                catalogGate.TryCheckpoint(TimeSpan.FromSeconds(30), guard.Token).ShouldBeTrue();
                await migration.WaitAsync(guard.Token);

                // Assert.
                var table = database.Catalog.Tables.Single(item => item.Name == "created");
                database.Catalog.GetIndexes(table.ObjectId).Count.ShouldBe(2);
                database.Catalog.GetIndexRegistrations().Count(item => item.ObjectId == table.ObjectId).ShouldBe(2);
                database.Catalog.GetPendingTableReclamations().ShouldBeEmpty();
            }
            finally
            {
                _output.WriteLine($"catalog pause stages={string.Join(',', stages)}; migration={migration.Status}; armed={armed.IsSet}");
                outside?.Rollback(); database.SchemaProvisioner.JointStage = null;
                database.CatalogStorage.CheckpointJournalSize = 0; catalogGate.JournalSizeLimit = 0;
                strategy.DurableStorageChanged = null;
            }
        });
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - Every build checkpoint peak fits the trigger plus one measured chunk (#1808)")]
    public async Task ExecuteAsync_IndexChunks_ShouldBoundEveryCheckpointPeak()
    {
        // Arrange.
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        const long trigger = 64 * 1024;
        var strategy = new CrashCaptureSqlStorageStrategy();
        var builder = SqlDatabaseEngine.CreateBuilder("sql-journal-chunks");
        builder.Options.StorageStrategy = strategy; builder.Options.CheckpointJournalSize = trigger;
        builder.Options.MaintenanceInterval = TimeSpan.FromHours(1); builder.Options.CheckpointInterval = TimeSpan.FromHours(1);
        await using var engine = await builder.BuildAsync(guard.Token);
        var database = await engine.CreateDatabaseAsync("typed", guard.Token);
        await using var writer = await database.CreateSessionAsync(guard.Token);
        await writer.ExecuteAsync("CREATE TABLE items(v INT)", cancellationToken: guard.Token);
        SeedRaw(database, [1], 8192); database.Coordinator.Checkpoint(guard.Token);
        using var events = new EventSourceRecorder(EventSource.GetSources().Single(source => source.Name == "Assimalign.Cohesion.Database.Storage"), EventLevel.Informational);
        bool inChunk = false;
        long start = 0, committedLength = 0;
        var commits = new List<long>(); var deltas = new List<long>();
        strategy.DurableStorageChanged = catalog =>
        { if (!catalog && inChunk && database.DataStorage.HasActiveTransactions) { committedLength = Math.Max(committedLength, database.DataStorage.JournalLength); } };
        SqlPlanExecutor.IndexBuildBoundary = (stage, _, length) =>
        {
            if (stage == "chunk-start") { start = length; committedLength = length; inChunk = true; }
            if (stage == "chunk-committed")
            { inChunk = false; commits.Add(committedLength); deltas.Add(committedLength - start); }
        };

        // Act.
        try { await writer.ExecuteAsync("CREATE INDEX ix ON items(v)", cancellationToken: guard.Token); }
        finally { SqlPlanExecutor.IndexBuildBoundary = null; strategy.DurableStorageChanged = null; }
        database.Coordinator.Checkpoint(guard.Token);

        // Assert: all data-file checkpoint starts, including creator lifecycle records.
        // Catalog publication has its own file and journal.
        commits.Count.ShouldBe(128);
        var peaks = events.Events.Where(item => item.EventId == 5 && Equals(item.Payload![0], database.DataStorage.Name.ToString()))
            .Select(item => (long)item.Payload![2]!).ToArray();
        peaks.ShouldNotBeEmpty();
        foreach (long peak in peaks) { peak.ShouldBeLessThanOrEqualTo(trigger + deltas.Max()); }
        _output.WriteLine($"SQL chunks={commits.Count}; largest delta={deltas.Max()}; checkpoint peak={peaks.Max()}; commits={string.Join(',', commits)}");
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - CREATE INDEX waits between chunks with size checkpoints disabled (#1808)")]
    public async Task ExecuteAsync_IndexAdmissionPause_ShouldRetryOutsideGate()
    {
        // Arrange.
        using var guard = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        await using var engine = SqlDatabaseEngine.Create("index-admission", new()
        { CheckpointJournalSize = 0, JournalSizeLimit = 8 * 1024 * 1024, MaintenanceInterval = TimeSpan.FromHours(1) });
        var database = await engine.CreateDatabaseAsync("typed", guard.Token);
        await using var writer = await database.CreateSessionAsync(guard.Token);
        await writer.ExecuteAsync("CREATE TABLE items(v VARCHAR(1000))", cancellationToken: guard.Token);
        SeedRaw(database, [new string('x', 980)], 80000);
        database.Coordinator.Checkpoint(guard.Token);
        using var recorder = new EventSourceRecorder(EventSource.GetSources().Single(source => source.Name == "Assimalign.Cohesion.Database.Transactions"), EventLevel.Verbose);

        // Act.
        await writer.ExecuteAsync("CREATE INDEX ix ON items(v)", cancellationToken: guard.Token);

        // Assert.
        database.Catalog.GetIndexes(Table(database).ObjectId).ShouldHaveSingleItem().Name.ShouldBe("ix");
        (await RowsAsync(writer, "SELECT COUNT(*) FROM items", guard.Token)).Single()[0].ShouldBe(80000L);
        recorder.Events.ShouldContain(item => item.EventId == 23 && Equals(item.Payload![0], database.DataStorage.Name.ToString()));
        database.IsOffline.ShouldBeFalse();
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - A held checkpoint makes the next ordinary index chunk wait and retry (#1808)")]
    public async Task ExecuteAsync_HeldCheckpointBetweenChunks_ShouldRetryWithoutAbandoningOwner()
    {
        // Arrange: keep the pause open so the checkpoint worker cannot win the race.
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        await using var engine = SqlDatabaseEngine.Create("held-index-admission", new()
        { CheckpointJournalSize = 0, MaintenanceInterval = TimeSpan.FromHours(1), CheckpointInterval = TimeSpan.FromHours(1) });
        var database = await engine.CreateDatabaseAsync("typed", guard.Token);
        await using var writer = await database.CreateSessionAsync(guard.Token);
        await writer.ExecuteAsync("CREATE TABLE items(v INT)", cancellationToken: guard.Token);
        SeedRaw(database, [1], 130);
        database.Coordinator.Checkpoint(guard.Token);
        var gate = TransactionCheckpointInterlock.ForStorage(database.DataStorage);
        using var holder = database.DataStorage.BeginTransaction("HoldIndexChunkCheckpoint");
        using var events = new EventSourceRecorder(EventSource.GetSources().Single(source => source.Name == "Assimalign.Cohesion.Database.Transactions"), EventLevel.Verbose);
        int committed = 0;
        bool armed = false;
        SqlPlanExecutor.IndexBuildBoundary = (stage, _, _) =>
        {
            if (stage == "chunk-committed") { Interlocked.Increment(ref committed); }
            if (stage != "chunk-before-commit" || armed) { return; }
            armed = true;
            database.DataStorage.CheckpointJournalSize = 1;
            gate.JournalSizeLimit = 2;
            gate.ObserveAdmissionPause().ShouldBeTrue();
        };
        var building = Task.Run(async () => await writer.ExecuteAsync("CREATE INDEX ix ON items(v)", cancellationToken: guard.Token), guard.Token);
        try
        {
            // Act: observe the actual refused/waiting admission, rather than a duration.
            while (!events.Events.Any(item => item.EventId == 25 && Equals(item.Payload![0], database.DataStorage.Name.ToString())))
            {
                if (building.IsCompleted) { await building; }
                await Task.Delay(1, guard.Token);
            }
            building.IsCompleted.ShouldBeFalse();
            Volatile.Read(ref committed).ShouldBe(1);
            gate.IsAdmissionPaused.ShouldBeTrue();
            gate.JournalSizeLimit = 0; holder.Rollback(); database.DataStorage.CheckpointJournalSize = 0;
            database.Coordinator.Checkpoint(guard.Token);
            await building.WaitAsync(guard.Token);

            // Assert: the retried chunk contributes each row once, and publication succeeds.
            committed.ShouldBe(3);
            var registration = database.Catalog.GetIndexRegistrations().ShouldHaveSingleItem();
            database.Catalog.GetPendingTableReclamations().ShouldBeEmpty();
            await ReadSurvivingTreeAsync(database, registration, 130, guard.Token);
            database.IsOffline.ShouldBeFalse();
        }
        finally
        {
            SqlPlanExecutor.IndexBuildBoundary = null;
            gate.JournalSizeLimit = 0; database.DataStorage.CheckpointJournalSize = 0; holder.Dispose();
            await building.WaitAsync(guard.Token);
        }
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - A cancelled multi-chunk build reclaims without reopening (#1808)")]
    public async Task ExecuteAsync_AbandonedBuild_ShouldReclaimCommittedChunksLive()
    {
        // Arrange.
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        await using var engine = SqlDatabaseEngine.Create("abandon-live", new() { MaintenanceInterval = TimeSpan.FromHours(1) });
        var database = await engine.CreateDatabaseAsync("typed", guard.Token);
        await using var writer = await database.CreateSessionAsync(guard.Token);
        await writer.ExecuteAsync("CREATE TABLE items(v INT)", cancellationToken: guard.Token);
        SeedRaw(database, [1], 256);
        using var buildCancellation = CancellationTokenSource.CreateLinkedTokenSource(guard.Token);
        ulong owner = 0; int chunks = 0;
        SqlPlanExecutor.IndexBuildBoundary = (stage, _, _) =>
        {
            if (stage != "chunk-committed") { return; }
            chunks++;
            if (chunks == 1)
            {
                // Exercise a full maintenance discovery while publication is pending.
                database.TableReclamation.Request();
                database.TableReclamation.RunBatch(guard.Token);
                database.TableReclamation.HasWork.ShouldBeFalse("prepared owners must not keep maintenance polling");
                return;
            }
            if (chunks != 2) { return; }
            owner = database.IndexManager.PageOwner(database.IndexManager.ExportRegistrations().Single());
            buildCancellation.Cancel();
        };

        // Act.
        try { await Should.ThrowAsync<OperationCanceledException>(async () => await writer.ExecuteAsync("CREATE INDEX ix ON items(v)", cancellationToken: buildCancellation.Token)); }
        finally { SqlPlanExecutor.IndexBuildBoundary = null; }
        await DrainRequestedReclamationAsync(database, guard.Token);

        // Assert.
        chunks.ShouldBe(2); owner.ShouldBeGreaterThan(0UL);
        database.DataStorage.GetOwnerIndexPages(owner).ShouldBeEmpty();
        database.Catalog.GetIndexRegistrations().ShouldBeEmpty();
        database.Catalog.GetPendingTableReclamations().ShouldBeEmpty();
        (await RowsAsync(writer, "SELECT COUNT(*) FROM items", guard.Token)).Single()[0].ShouldBe(256L);
    }
}
