using System;
using System.Diagnostics.Tracing;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Sql.Catalog;
using Assimalign.Cohesion.Database.Sql.Internal;
using Assimalign.Cohesion.Database.Sql.Schema;
using Assimalign.Cohesion.Database.Sql.Tests.TestObjects;
using Assimalign.Cohesion.Database.Storage;
using Assimalign.Cohesion.Database.Transactions;
using Assimalign.Cohesion.Database.Types;
using Assimalign.Cohesion.Testing;

namespace Assimalign.Cohesion.Database.Sql.Tests;

public sealed partial class SqlJointTypeMigrationTests
{
    [Theory(DisplayName = "Cohesion Test [Sql] - Joint deferred cost: same-name index replacement keeps the heap (#1808)")]
    [InlineData(8192)]
    [InlineData(65536)]
    public async Task ApplyAsync_DeferredIndexReplacement_ShouldKeepHeapAndWriteOnlyTree(int rows)
    {
        // Arrange: replacing ix(w) with ix(w,v) retains the heap and other physical trees.
        using var guard = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        SqlCompiledSchema Declare(bool target) => Compiled(new CompiledSchemaTable("items", "row",
            [new("v", DatabaseType.Int32, false), new("w", DatabaseType.Int32, true)], new("pk", ["v"]),
            [new("ix", target ? ["w", "v"] : ["w"], false)], []));
        var storage = new CrashCaptureSqlStorageStrategy();
        await using (var seed = await Builder(storage).BuildAsync(guard.Token))
        {
            var database = await seed.CreateDatabaseAsync("typed", guard.Token);
            await database.ApplySchemaAsync(Declare(false), guard.Token);
            await using var writer = await database.CreateSessionAsync(guard.Token);
            for (int first = 0; first < rows; first += 64)
            { await writer.ExecuteAsync("INSERT INTO items VALUES " + string.Join(',', Enumerable.Range(first, 64).Select(id => $"({id},{id})")), cancellationToken: guard.Token); }
            database.Coordinator.Checkpoint();
            storage = storage.CaptureDurableImages();
        }
        // Act
        var measured = await MeasureDeferredCostAsync(storage, Declare(false), Declare(true), guard.Token);
        // Assert: the shared build path replaces only the requested tree.
        measured.Rewritten.ShouldBe(0);
        measured.KeepsOwner.ShouldBeTrue();
        measured.KeepsHeap.ShouldBeTrue();
        _output.WriteLine($"Index replacement ({rows} rows): 49a={measured.OldPages}; joint={measured.JointPages}; ratio={(double)measured.JointPages / measured.OldPages:P2}");
        measured.Reclaimed[0].ShouldBeGreaterThan(0);
        measured.Reclaimed[1].ShouldBe(measured.Reclaimed[0]);
        (measured.JointPages - measured.Reclaimed[1]).ShouldBeLessThanOrEqualTo((measured.OldPages - measured.Reclaimed[0]) * 110 / 100);
        measured.JointPages.ShouldBeLessThanOrEqualTo(measured.OldPages * 110 / 100);
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - Joint deferred cost: nullable append inside another table's deferred plan keeps the heap")]
    public async Task ApplyAsync_DeferredNullableAppend_ShouldKeepPopulatedOrders()
    {
        // Arrange: SQ-A6 extends KeepOwner to steps that were metadata-only at base.
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        SqlCompiledSchema Declare(bool target) => Compiled(
            new("orders", "orders", target ? [new("id", DatabaseType.Int32, false), new("note", DatabaseType.String, true)] : [new("id", DatabaseType.Int32, false)], new("pk", ["id"]), [], []),
            new("audit", "audit", [new("id", DatabaseType.Int32, false)], new("pk", ["id"]), target ? [new("ix", ["id"], false)] : [], []));
        await using var engine = await Builder(new()).BuildAsync(guard.Token);
        var database = await engine.CreateDatabaseAsync("typed", guard.Token);
        await database.ApplySchemaAsync(Declare(false), guard.Token);
        await using var writer = await database.CreateSessionAsync(guard.Token);
        await writer.ExecuteAsync("INSERT INTO orders VALUES(1),(2),(3)", cancellationToken: guard.Token);
        await writer.ExecuteAsync("INSERT INTO audit VALUES(1)", cancellationToken: guard.Token);
        long rewritten = 0;
        database.SchemaProvisioner.JointStage = phase =>
        { if (phase == "IntentDurable") { database.Catalog.PendingMigration!.Steps.ShouldContain(step => step.Table == "orders" && step.Kind == "Joint/KeepOwner"); } };
        SqlPlanExecutor.RewriteStage = (phase, count) => { if (phase == "Copied") { rewritten = count; } };
        try
        {
            // Act
            await database.SchemaProvisioner.ApplyAsync(Declare(true), guard.Token, deferDataScalingSteps: true);
            await WaitDoneAsync(database, guard.Token);
            // Assert
            rewritten.ShouldBe(0);
            (await RowsAsync(writer, "SELECT note FROM orders", guard.Token)).Length.ShouldBe(3);
        }
        finally { SqlPlanExecutor.RewriteStage = null; }
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - Joint deferred cost: shuffled distinct keys cross copy checkpoints within ten percent of 49a")]
    public async Task ApplyAsync_DeferredFixShuffledIndexCost_ShouldMeasureCopyCheckpoints()
    {
        // Arrange: a deterministic permutation spreads each bracket over many leaves.
        const int rows = 524288;
        const int createdRows = 393216;
        using var guard = new CancellationTokenSource(TimeSpan.FromMinutes(6));
        SqlCompiledSchema Declare(bool target, bool created = false) => SqlSchema.Create("typed", schema =>
        {
            schema.Table<OtherRow>("items", table =>
            { table.Key(row => row.Id); if (target) { table.Index(row => row.Id); } });
            if (target && created) { schema.Table<DeferredCreated>("created", table => table.Key(row => row.Id)); }
        }).Compile();
        var storage = new CrashCaptureSqlStorageStrategy();
        CrashCaptureSqlStorageStrategy? smaller = null;
        await using (var seed = await Builder(storage).BuildAsync(guard.Token))
        {
            var database = await seed.CreateDatabaseAsync("typed", guard.Token);
            await database.ApplySchemaAsync(Declare(false), guard.Token);
            await SeedShuffledIndexRowsAsync(database, rows, guard.Token, copied =>
            {
                if (copied != createdRows) { return; }
                database.Coordinator.Checkpoint(guard.Token);
                smaller = storage.CaptureDurableImages();
            });
            database.Coordinator.Checkpoint();
            storage = storage.CaptureDurableImages();
        }
        // Act: the second target also creates a keyed table, exercising its
        // eligibility for the larger threshold at a size that crosses checkpoints.
        var measurements = new[]
        {
            await MeasureDeferredCostAsync(storage, Declare(false), Declare(true), guard.Token),
            await MeasureDeferredCostAsync(smaller.ShouldNotBeNull(), Declare(false), Declare(true, true), guard.Token),
        };
        for (int i = 0; i < measurements.Length; i++)
        {
            var measured = measurements[i];
            _output.WriteLine($"Shuffled {(i == 0 ? rows : createdRows)}-row PK additive index, new table={i == 1} (64 MiB engine checkpoint budget): 49a={measured.OldPages}; joint={measured.JointPages}; rewritten={measured.Rewritten}; ratio={(double)measured.JointPages / measured.OldPages:P2}; copy-phase checkpoints={measured.Checkpoints}; forced copy checkpoints={measured.ForcedCheckpoints}");
        }
        // Assert
        foreach (var measured in measurements)
        {
            measured.Rewritten.ShouldBe(0);
            measured.KeepsOwner.ShouldBeTrue();
            measured.KeepsHeap.ShouldBeTrue();
            measured.Threshold.ShouldBe(64L * 1024 * 1024);
            measured.Checkpoints.ShouldBeGreaterThan(0);
            // The engine services its size trigger at gate release. Nit 9 avoids
            // following that checkpoint with a forced copy checkpoint on the empty WAL.
            measured.ForcedCheckpoints.ShouldBe(0);
            measured.JointPages.ShouldBeLessThanOrEqualTo(measured.OldPages * 110 / 100);
        }
    }

    private static async Task SeedShuffledIndexRowsAsync(SqlDatabase database, int rows, CancellationToken token, Action<int>? afterBracket = null)
    {
        var keys = Enumerable.Range(0, rows).ToArray();
        new Random(1778).Shuffle(keys);
        await using var writer = await database.CreateSessionAsync(token);
        for (int first = 0; first < rows; first += 64)
        {
            await writer.ExecuteAsync("INSERT INTO items VALUES " + string.Join(',', keys.Skip(first).Take(64)
                .Select(key => $"({key})")), cancellationToken: token);
            if ((first + 64) % 4096 == 0) { database.Coordinator.Checkpoint(token); }
            afterBracket?.Invoke(first + 64);
        }
    }

    private async Task<(long OldPages, long JointPages, long Rewritten, int Checkpoints, bool KeepsHeap, bool KeepsOwner, long Threshold, int ForcedCheckpoints, long[] Reclaimed)> MeasureDeferredCostAsync(
        CrashCaptureSqlStorageStrategy storage, SqlCompiledSchema source, SqlCompiledSchema target, CancellationToken token)
    {
        var pages = new long[2];
        var reclaimedByMode = new long[2];
        long rewritten = 0;
        int checkpoints = 0;
        int observedCheckpoints = 0;
        bool keepsHeap = false, keepsOwner = false;
        long threshold = 0;
        for (int mode = 0; mode < 2; mode++)
        {
            var image = storage.CaptureDurableImages();
            var builder = Builder(image);
            builder.Options.CheckpointJournalSize = 64L * 1024 * 1024;
            await using var engine = await builder.BuildAsync(token);
            var database = await engine.OpenDatabaseAsync("typed", token);
            database.Coordinator.Checkpoint();
            database.CatalogStorage.Checkpoint();
            var oldOwners = database.IndexManager.ExportRegistrations().Select(entry => database.IndexManager.PageOwner(entry)).ToHashSet();
            long reclaimed = 0;
            int batches = 0;
            database.TableReclamation.AfterBatch = (owner, count) =>
            { if (oldOwners.Contains(owner)) { Interlocked.Add(ref reclaimed, count); Interlocked.Increment(ref batches); } };
            long before = image.PageWrites("typed");
            ulong originalHeap = Table(database).ObjectId;
            using var checkpointEvents = new EventSourceRecorder(EventSource.GetSources().Single(source =>
                source.Name == "Assimalign.Cohesion.Database.Storage"), EventLevel.Informational);
            SqlPlanExecutor.RewriteStage = (phase, count) =>
            { if (phase == "Copied") { rewritten = count; } if (phase == "CopyCheckpointed") { checkpoints++; } };
            try
            {
                if (mode == 0)
                {
                    var script = SqlMigrationScriptGenerator.Generate(SqlSchemaMigrationPlanner.Plan(source, target), source);
                    await database.Catalog.SavePendingMigrationAsync(new(target.Hash, target.CanonicalDocument,
                        script.Steps.Select((step, index) => new SqlCatalogMigrationStep(step.Kind, "items", step.Operation.ObjectName, index + 1)).ToArray(), script.Steps.Count), token);
                    var locks = await database.Coordinator.BeginAsync(IsolationLevel.ReadCommitted, token);
                    try
                    {
                        await using var session = database.CreateSchemaSession("typed", token, locks.Sequence);
                        session.ProvisioningProgress = (_, _) => { };
                        foreach (var step in script.Steps) { await session.ExecuteAsync(step.Request, token); }
                        await database.Catalog.SaveSchemaStateAsync(new(target.Hash, target.CanonicalDocument, database.Catalog.DdlGeneration), token);
                    }
                    finally { await database.Coordinator.RollbackAsync(locks, CancellationToken.None); }
                }
                else
                {
                    database.SchemaProvisioner.JointStage = phase =>
                    {
                        if (phase != "IntentDurable") { return; }
                        keepsOwner = database.Catalog.PendingMigration!.Steps.Any(step => step.Kind == "Joint/KeepOwner" && step.Table == "items");
                        if (source.Tables[0].Indexes.Count == 0) { return; }
                        database.Catalog.PendingMigration!.Steps.ShouldContain(step => step.Kind == "Joint/KeepOwner" && step.Table == "items");
                    };
                    await database.SchemaProvisioner.ApplyAsync(target, token, deferDataScalingSteps: true);
                    await WaitDoneAsync(database, token);
                    database.Catalog.SchemaState!.ContentHash.ShouldBe(target.Hash);
                    database.SchemaProvisioner.JointMetrics!.SelfCommits.ShouldBe(1);
                    keepsHeap = Table(database).ObjectId == originalHeap;
                    threshold = database.SchemaProvisioner.JointCopyCheckpointThreshold;
                    observedCheckpoints = checkpointEvents.Events.Count(entry => entry.EventId == 5 &&
                        Equals(entry.Payload![0], database.DataStorage.Name.ToString()));
                }
                // Include the complete replacement cost in both modes, including
                // freeing old trees, before either final checkpoint is sampled.
                await DrainRequestedReclamationAsync(database, token);
                database.Coordinator.Checkpoint();
                database.CatalogStorage.Checkpoint();
                pages[mode] = image.PageWrites("typed") - before;
                reclaimedByMode[mode] = reclaimed;
                _output.WriteLine($"Reclamation drained, mode={mode}: pages={pages[mode]}, reclaimed={reclaimed}, batches={batches}");
            }
            finally { SqlPlanExecutor.RewriteStage = null; }
        }
        return (pages[0], pages[1], rewritten, observedCheckpoints, keepsHeap, keepsOwner, threshold, checkpoints, reclaimedByMode);
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - Joint deferred cost: kept-owner checkpoint threshold falls back on a small volume or disabled engine threshold")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ApplyAsync_DeferredFixCheckpointBudget_ShouldFallBackWithoutRefusal(bool disabled)
    {
        // Arrange
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        var storage = new CrashCaptureSqlStorageStrategy();
        var builder = Builder(storage);
        builder.Options.CheckpointJournalSize = disabled ? 0 : 256L * 1024 * 1024;
        await using var engine = await builder.BuildAsync(guard.Token);
        var database = await engine.CreateDatabaseAsync("typed", guard.Token);
        var source = SqlSchema.Create("typed", schema => schema.Table<OtherRow>("items", table => table.Key(row => row.Id))).Compile();
        var target = ChangeItems(source, indexes: [new("ix", ["Id"], false)]);
        await database.ApplySchemaAsync(source, guard.Token);
        await SeedShuffledIndexRowsAsync(database, 65536, guard.Token);
        // Enough for the 16 MiB admission term plus reserves, below the preferred term.
        if (!disabled) { storage.VolumeCapacityBytes = storage.AllocatedBytes + 192L * 1024 * 1024; }
        database.Coordinator.Checkpoint(guard.Token);
        int checkpoints = 0, observed = 0;
        long peak = 0, largestBracket = 0, last = database.DataStorage.JournalLength;
        storage.DurableStorageChanged = catalog =>
        {
            if (catalog) { return; }
            long length = database.DataStorage.JournalLength;
            peak = Math.Max(peak, length);
            largestBracket = Math.Max(largestBracket, length - last);
        };
        database.SchemaProvisioner.JointStage = stage =>
        {
            if (stage != "CatalogPublished") { return; }
            storage.DurableStorageChanged?.Invoke(false);
            storage.DurableStorageChanged = null;
        };
        SqlPlanExecutor.RewriteStage = (phase, bytes) =>
        {
            if (phase == "CopyCommitted")
            {
                peak = Math.Max(peak, bytes);
                last = bytes;
            }
            if (phase == "CopyCheckpointObserved") { observed++; }
            if (phase == "CopyCheckpointed") { checkpoints++; last = database.DataStorage.JournalLength; }
        };
        try
        {
            // Act
            await database.SchemaProvisioner.ApplyAsync(target, guard.Token, deferDataScalingSteps: true);
            await WaitDoneAsync(database, guard.Token);

            // Assert: actual copy checkpoints prove the smaller threshold was used.
            _output.WriteLine($"Checkpoint fallback: disabled={disabled}; admission={database.SchemaProvisioner.JointSpaceNeed}; checkpoints={checkpoints}; observed={observed}; peak WAL={peak}; largest bracket={largestBracket}");
            checkpoints.ShouldBeGreaterThan(0);
            peak.ShouldBeGreaterThan(0);
            largestBracket.ShouldBeGreaterThan(0);
            database.SchemaProvisioner.JointCopyCheckpointThreshold.ShouldBe(SqlSchemaProvisioner.JointCopyCheckpointBytes);
            peak.ShouldBeLessThanOrEqualTo(SqlSchemaProvisioner.JointCopyCheckpointBytes + largestBracket);
            observed.ShouldBe(0);
            database.SchemaProvisioner.JointSpaceNeed.ShouldBeLessThan(192L * 1024 * 1024);
            database.Catalog.SchemaState!.ContentHash.ShouldBe(target.Hash);
            database.Provisioning.ShouldBeNull();
            storage.CapacityFailures.ShouldBe(0);
        }
        finally { SqlPlanExecutor.RewriteStage = null; storage.DurableStorageChanged = null; database.SchemaProvisioner.JointStage = null; }
    }
}
