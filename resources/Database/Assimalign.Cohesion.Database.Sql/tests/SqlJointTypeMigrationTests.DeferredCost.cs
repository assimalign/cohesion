using System;
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

namespace Assimalign.Cohesion.Database.Sql.Tests;

public sealed partial class SqlJointTypeMigrationTests
{
    [Fact(DisplayName = "Cohesion Test [Sql] - Joint deferred cost: heap-owned index replacement rewrites until #1808")]
    public async Task ApplyAsync_DeferredFixHeapOwnedIndexUntil1808_ShouldMeasureWholeTableRewrite()
    {
        // Arrange: replacing ix(w) with ix(w,v) rewrites until #1808 gives every tree its own owner.
        const int rows = 8192;
        using var guard = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        SqlCompiledSchema Declare(bool target) => Compiled(new CompiledSchemaTable("items", "row",
            [new("v", DatabaseType.Int32, false), new("w", DatabaseType.Int32, true)], null,
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
        // Assert: pin the accepted temporary rewrite, without a ratio bound.
        measured.Rewritten.ShouldBe(rows);
        _output.WriteLine($"Heap-owned replacement ({rows} rows, until #1808): 49a={measured.OldPages}; joint={measured.JointPages}; ratio={(double)measured.JointPages / measured.OldPages:P2}");
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - Joint deferred cost: nullable append inside another table's deferred plan rewrites until SQ-A6")]
    public async Task ApplyAsync_DeferredFixNullableAppendUntilA6_ShouldRewritePopulatedOrders()
    {
        // Arrange: SQ-A6 extends KeepOwner to steps that were metadata-only at base.
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        SqlCompiledSchema Declare(bool target) => Compiled(
            new("orders", "orders", target ? [new("id", DatabaseType.Int32, false), new("note", DatabaseType.String, true)] : [new("id", DatabaseType.Int32, false)], null, [], []),
            new("audit", "audit", [new("id", DatabaseType.Int32, false)], null, target ? [new("ix", ["id"], false)] : [], []));
        await using var engine = await Builder(new()).BuildAsync(guard.Token);
        var database = await engine.CreateDatabaseAsync("typed", guard.Token);
        await database.ApplySchemaAsync(Declare(false), guard.Token);
        await using var writer = await database.CreateSessionAsync(guard.Token);
        await writer.ExecuteAsync("INSERT INTO orders VALUES(1),(2),(3)", cancellationToken: guard.Token);
        await writer.ExecuteAsync("INSERT INTO audit VALUES(1)", cancellationToken: guard.Token);
        long rewritten = 0;
        database.SchemaProvisioner.JointStage = phase =>
        { if (phase == "IntentDurable") { database.Catalog.PendingMigration!.Steps.ShouldContain(step => step.Table == "orders" && step.Kind == "Joint/RewriteOwner"); } };
        SqlPlanExecutor.RewriteStage = (phase, count) => { if (phase == "Copied") { rewritten = count; } };
        try
        {
            // Act
            await database.SchemaProvisioner.ApplyAsync(Declare(true), guard.Token, deferDataScalingSteps: true);
            await WaitDoneAsync(database, guard.Token);
            // Assert
            rewritten.ShouldBe(3);
            (await RowsAsync(writer, "SELECT note FROM orders", guard.Token)).Length.ShouldBe(3);
        }
        finally { SqlPlanExecutor.RewriteStage = null; }
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - Joint deferred cost: shuffled distinct keys cross copy checkpoints within ten percent of 49a")]
    public async Task ApplyAsync_DeferredFixShuffledIndexCost_ShouldMeasureCopyCheckpoints()
    {
        // Arrange: a deterministic permutation spreads each bracket over many leaves.
        const int rows = 262144;
        using var guard = new CancellationTokenSource(TimeSpan.FromMinutes(4));
        SqlCompiledSchema Declare(bool target) => Compiled(new CompiledSchemaTable("items", "row", [new("v", DatabaseType.Int32, false)], null, target ? [new("ix", ["v"], false)] : [], []));
        var storage = new CrashCaptureSqlStorageStrategy();
        await using (var seed = await Builder(storage).BuildAsync(guard.Token))
        {
            var database = await seed.CreateDatabaseAsync("typed", guard.Token);
            await database.ApplySchemaAsync(Declare(false), guard.Token);
            SeedShuffledIndexRows(database, rows, guard.Token);
            database.Coordinator.Checkpoint();
            storage = storage.CaptureDurableImages();
        }
        // Act
        var measured = await MeasureDeferredCostAsync(storage, Declare(false), Declare(true), guard.Token);
        // Assert
        measured.Rewritten.ShouldBe(0);
        measured.Checkpoints.ShouldBeGreaterThan(0);
        _output.WriteLine($"Shuffled {rows}-row additive index (64 MiB engine checkpoint budget): 49a={measured.OldPages}; joint={measured.JointPages}; ratio={(double)measured.JointPages / measured.OldPages:P2}; copy checkpoints={measured.Checkpoints}");
        measured.JointPages.ShouldBeLessThanOrEqualTo(measured.OldPages * 110 / 100);
    }

    private static void SeedShuffledIndexRows(SqlDatabase database, int rows, CancellationToken token)
    {
        var keys = Enumerable.Range(0, rows).ToArray();
        new Random(1778).Shuffle(keys);
        var table = Table(database);
        for (int first = 0; first < rows; first += 64)
        {
            token.ThrowIfCancellationRequested();
            using var bracket = database.DataStorage.BeginTransaction();
            for (int row = first; row < first + 64; row++)
            { database.DataStorage.InsertRow(bracket, table.ObjectId, SqlRowCodec.Encode(table, [keys[row]], TransactionSequence.None)); }
            bracket.Commit();
            if ((first + 64) % 4096 == 0) { database.Coordinator.Checkpoint(); }
        }
    }

    private static async Task<(long OldPages, long JointPages, long Rewritten, int Checkpoints)> MeasureDeferredCostAsync(
        CrashCaptureSqlStorageStrategy storage, SqlCompiledSchema source, SqlCompiledSchema target, CancellationToken token)
    {
        var pages = new long[2];
        long rewritten = 0;
        int checkpoints = 0;
        for (int mode = 0; mode < 2; mode++)
        {
            var image = storage.CaptureDurableImages();
            var builder = Builder(image);
            builder.Options.CheckpointJournalSize = 64L * 1024 * 1024;
            await using var engine = await builder.BuildAsync(token);
            var database = await engine.OpenDatabaseAsync("typed", token);
            database.Coordinator.Checkpoint();
            database.CatalogStorage.Checkpoint();
            long before = image.PageWrites("typed");
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
                        if (phase != "IntentDurable" || source.Tables[0].Indexes.Count == 0) { return; }
                        database.Catalog.PendingMigration!.Steps.ShouldContain(step => step.Kind == "Joint/RewriteOwner" && step.Table == "items");
                    };
                    await database.SchemaProvisioner.ApplyAsync(target, token, deferDataScalingSteps: true);
                    await WaitDoneAsync(database, token);
                    database.Catalog.SchemaState!.ContentHash.ShouldBe(target.Hash);
                    database.SchemaProvisioner.JointMetrics!.SelfCommits.ShouldBe(1);
                }
                database.Coordinator.Checkpoint();
                database.CatalogStorage.Checkpoint();
                pages[mode] = image.PageWrites("typed") - before;
            }
            finally { SqlPlanExecutor.RewriteStage = null; }
        }
        return (pages[0], pages[1], rewritten, checkpoints);
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
        var source = Compiled(new CompiledSchemaTable("items", "row", [new("v", DatabaseType.Int32, false)], null, [], []));
        var target = ChangeItems(source, indexes: [new("ix", ["v"], false)]);
        await database.ApplySchemaAsync(source, guard.Token);
        SeedShuffledIndexRows(database, 65536, guard.Token);
        // Enough for the 16 MiB admission term plus reserves, below the preferred term.
        if (!disabled) { storage.VolumeCapacityBytes = storage.AllocatedBytes + 192L * 1024 * 1024; }
        int checkpoints = 0;
        long peak = 0;
        SqlPlanExecutor.RewriteStage = (phase, bytes) =>
        { if (phase == "CopyCheckpointed") { checkpoints++; } if (phase == "CopyCommitted") { peak = Math.Max(peak, bytes); } };
        try
        {
            // Act
            await database.SchemaProvisioner.ApplyAsync(target, guard.Token, deferDataScalingSteps: true);
            await WaitDoneAsync(database, guard.Token);
            // Assert: actual copy checkpoints prove the smaller threshold was used.
            checkpoints.ShouldBeGreaterThan(0);
            peak.ShouldBeLessThan(64L * 1024 * 1024);
            database.SchemaProvisioner.JointSpaceNeed.ShouldBeLessThan(192L * 1024 * 1024);
            database.Catalog.SchemaState!.ContentHash.ShouldBe(target.Hash);
            database.Provisioning.ShouldBeNull();
            storage.CapacityFailures.ShouldBe(0);
            _output.WriteLine($"Checkpoint fallback: disabled={disabled}; admission={database.SchemaProvisioner.JointSpaceNeed}; checkpoints={checkpoints}; peak WAL={peak}");
        }
        finally { SqlPlanExecutor.RewriteStage = null; }
    }
}
