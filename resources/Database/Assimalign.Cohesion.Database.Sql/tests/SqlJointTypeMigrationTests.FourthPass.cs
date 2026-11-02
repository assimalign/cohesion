using System;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Sql.Catalog;
using Assimalign.Cohesion.Database.Sql.Internal;
using Assimalign.Cohesion.Database.Sql.Schema;
using Assimalign.Cohesion.Database.Sql.Tests.TestObjects;
using Assimalign.Cohesion.Database.Storage.Units;
using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Sql.Tests;

public sealed partial class SqlJointTypeMigrationTests
{
    private static async Task DrainReclamationAsync(SqlDatabase database, CancellationToken token)
    {
        database.TableReclamation.Request();
        while (database.TableReclamation.HasWork)
        {
            token.ThrowIfCancellationRequested();
            database.TableReclamation.RunBatch(token);
            await Task.Yield();
        }
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - Joint reserve: engine dirty capacity and reclamation survive an exactly admitted publication")]
    public async Task ApplyAsync_ExactlyAdmittedVolume_ShouldReclaimOldOwnersOnline()
    {
        // Arrange
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(180));
        var storage = new CrashCaptureSqlStorageStrategy();
        var builder = Builder(storage);
        builder.Options.BufferPoolCapacity = 1024 * 1024;
        await using var engine = await builder.BuildAsync(guard.Token);
        var database = await engine.CreateDatabaseAsync("typed", guard.Token);
        SqlCompiledSchema Declare(bool changed) => Compiled(new CompiledSchemaTable("items", "row",
            [new("Value", changed ? DatabaseType.Int64 : DatabaseType.Int32, false), new("Payload", DatabaseType.String, false)], null, [], []));
        await database.ApplySchemaAsync(Declare(false), guard.Token);
        SeedRaw(database, [1, new string('x', 7000)], 8192);
        database.Coordinator.Checkpoint(guard.Token);
        long oldPages = database.DataStorage.GetOwnerPages(Table(database).ObjectId).Count;
        long reclaimed = database.TableReclamation.ReclaimedPages;
        database.SchemaProvisioner.JointStage = stage =>
        {
            if (stage == "OwnersDurable")
            { storage.VolumeCapacityBytes = storage.AllocatedBytes + database.SchemaProvisioner.JointSpaceNeed; }
        };
        try
        {
            // Act
            await database.ApplySchemaAsync(Declare(true), guard.Token);
            await DrainReclamationAsync(database, guard.Token);
            database.Coordinator.Checkpoint(guard.Token);

            // Assert
            database.TableReclamation.ReclaimedPages.ShouldBeGreaterThanOrEqualTo(reclaimed + oldPages);
            storage.CapacityFailures.ShouldBe(0);
            database.IsOffline.ShouldBeFalse();
            database.Catalog.SchemaState!.ContentHash.ShouldBe(Declare(true).Hash);
            _output.WriteLine($"success N={database.SchemaProvisioner.JointSpaceNeed}; capacity={storage.VolumeCapacityBytes}; peak={storage.PeakAllocatedBytes}; reclaimed={database.TableReclamation.ReclaimedPages - reclaimed}");
        }
        finally { storage.VolumeCapacityBytes = long.MaxValue; }
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - Joint watchdog: sparse Unix allocations stop online before their flush")]
    public async Task ApplyAsync_UnwrittenSparsePages_ShouldStopOnlineBeforeFlush()
    {
        // Arrange
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(120));
        var storage = new CrashCaptureSqlStorageStrategy { UnixSparseGrowth = true };
        var builder = Builder(storage);
        builder.Options.PageWriteBackInterval = TimeSpan.FromDays(1);
        builder.Options.CheckpointJournalSize = 0;
        builder.Options.CheckpointInterval = TimeSpan.FromDays(1);
        await using var engine = await builder.BuildAsync(guard.Token);
        var database = await engine.CreateDatabaseAsync("typed", guard.Token);
        await database.ApplySchemaAsync(WideCheckpointDeclaration(false), guard.Token);
        SeedRaw(database, [1, new string('x', 4000)], 4096);
        database.Coordinator.Checkpoint(guard.Token);
        long stoppedAfter = 0;
        SqlPlanExecutor.RewriteStage = (stage, count) =>
        {
            if (stage == "Copied" && count == 1024)
            {
                stoppedAfter = count;
                // More than the old D+S, less than the unflushed copy plus its
                // next WAL. ftruncate has charged none of these prepared pages.
                storage.VolumeCapacityBytes = storage.AllocatedBytes + 5L * 1024 * 1024;
            }
        };
        try
        {
            // Act
            var failure = await Should.ThrowAsync<SqlSchemaMigrationException>(async () =>
                await database.ApplySchemaAsync(WideCheckpointDeclaration(true), guard.Token));
            await DrainReclamationAsync(database, guard.Token);

            // Assert
            database.Coordinator.Checkpoint(guard.Token);
            stoppedAfter.ShouldBe(1024);
            failure.Diagnostics.ShouldContain(diagnostic => diagnostic.Code == "CS03402");
            storage.CapacityFailures.ShouldBe(0);
            database.IsOffline.ShouldBeFalse();
            database.Catalog.SchemaState!.ContentHash.ShouldBe(WideCheckpointDeclaration(false).Hash);
        }
        finally { SqlPlanExecutor.RewriteStage = null; storage.VolumeCapacityBytes = long.MaxValue; }
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - Joint bounds: small indexed tables fit without fanout-three row-count heights")]
    public async Task ApplyAsync_SmallIndexedTable_ShouldFitEightyMiB()
    {
        // Arrange
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var storage = await SeedAsync(guard.Token, 100);
        storage.MigrationAvailableBytes = 80L * 1024 * 1024;
        await using var engine = await Builder(storage).BuildAsync(guard.Token);
        var database = await engine.OpenDatabaseAsync("typed", guard.Token);

        // Act
        await database.ApplySchemaAsync(Target().Compile(), guard.Token);

        // Assert
        database.SchemaProvisioner.JointSpaceNeed.ShouldBeLessThanOrEqualTo(storage.MigrationAvailableBytes);
        database.Catalog.SchemaState!.ContentHash.ShouldBe(Target().Compile().Hash);
        database.IsOffline.ShouldBeFalse();
        _output.WriteLine($"100 rows, two indexes N={database.SchemaProvisioner.JointSpaceNeed}");
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - Joint pending pages: engine-flushed copy pages cannot exceed the dirty pool charge")]
    public async Task ApplyAsync_EngineFlushedCopy_ShouldCapUnwrittenPagesAtPoolCapacity()
    {
        // Arrange
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        var storage = new CrashCaptureSqlStorageStrategy();
        var builder = Builder(storage);
        builder.Options.BufferPoolCapacity = 1024 * 1024;
        await using var engine = await builder.BuildAsync(guard.Token);
        var database = await engine.CreateDatabaseAsync("typed", guard.Token);
        await database.ApplySchemaAsync(WideCheckpointDeclaration(false), guard.Token);
        SeedRaw(database, [1, new string('x', 4000)], 4096);
        database.Coordinator.Checkpoint(guard.Token);
        int checkpoints = 0;
        SqlPlanExecutor.RewriteStage = (stage, count) =>
        {
            if (stage != "Copied") { return; }
            // This is an engine checkpoint, outside the copy's accounting reset.
            database.Coordinator.Checkpoint(guard.Token);
            checkpoints++;
            if (count == 512)
            { storage.MigrationAvailableBytes = database.SchemaProvisioner.JointSpaceDiscardBytes + 6L * 1024 * 1024; }
        };
        try
        {
            // Act
            await database.ApplySchemaAsync(WideCheckpointDeclaration(true), guard.Token);

            // Assert
            checkpoints.ShouldBe(64);
            database.Catalog.SchemaState!.ContentHash.ShouldBe(WideCheckpointDeclaration(true).Hash);
            database.IsOffline.ShouldBeFalse();
        }
        finally { SqlPlanExecutor.RewriteStage = null; storage.MigrationAvailableBytes = long.MaxValue; }
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - Joint publication: post-validation space loss discards online")]
    [InlineData("Validated")]
    [InlineData("Publishing")]
    public async Task ApplyAsync_PublicationSpaceLoss_ShouldDiscardOnline(string phase)
    {
        // Arrange
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var storage = await SeedAsync(guard.Token);
        await using var engine = await Builder(storage).BuildAsync(guard.Token);
        var database = await engine.OpenDatabaseAsync("typed", guard.Token);
        var previous = Table(database);
        database.SchemaProvisioner.JointStage = stage =>
        { if (stage == phase) { storage.MigrationAvailableBytes = database.SchemaProvisioner.JointSpaceDiscardBytes; } };

        // Act
        var failure = await Should.ThrowAsync<SqlSchemaMigrationException>(async () =>
            await database.ApplySchemaAsync(Target().Compile(), guard.Token));

        // Assert
        failure.Diagnostics.ShouldContain(diagnostic => diagnostic.Code == "CS03402");
        failure.Message.ShouldContain("bytes needed");
        Table(database).ShouldBeSameAs(previous);
        database.Catalog.SchemaState!.ContentHash.ShouldBe(Source().Compile().Hash);
        database.Catalog.PendingMigration.ShouldBeNull();
        database.IsOffline.ShouldBeFalse();
        storage.MigrationAvailableBytes = long.MaxValue;
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - Joint estimate: expression defaults include the result type's fixed width")]
    public void JointColumnBytes_ExpressionDefault_ShouldCoverEncodedTimestamp()
    {
        // Arrange: typed declarations currently admit literal defaults only. Exercise
        // the estimator's defensive expression branch without broadening that policy.
        var shape = new SqlCatalogTable(1, "dbo", "items",
            [new SqlCatalogColumn("Created", new(DatabaseType.DateTime))], [], DatabaseObjectOwner.Schema, "typed");
        var item = new SqlJointMigrationTable(null, shape, [], [new SqlBoundColumn(0)]);

        // Act
        long bytes = EstimateColumn(null!, item, 0, [], [], 4096, false);

        // Assert
        bytes.ShouldBe(4096L * 10);
    }

    [UnsafeAccessor(UnsafeAccessorKind.StaticMethod, Name = "JointColumnBytes")]
    private static extern long EstimateColumn(SqlSchemaProvisioner owner, SqlJointMigrationTable item,
        int ordinal, long[] measured, long[] nonNullRows, long rows, bool key);

    [Theory(DisplayName = "Cohesion Test [Sql] - Joint estimate: JSONB text output includes its collation byte")]
    [InlineData(DatabaseType.String)]
    [InlineData(DatabaseType.Json)]
    public async Task ApplyAsync_JsonbText_ShouldChargeCollationByte(DatabaseType targetType)
    {
        // Arrange
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var storage = new CrashCaptureSqlStorageStrategy();
        await using var engine = await Builder(storage).BuildAsync(guard.Token);
        var database = await engine.CreateDatabaseAsync("typed", guard.Token);
        SqlCompiledSchema Declare(DatabaseType type) => Compiled(new CompiledSchemaTable("items", "row", [new("Code", type, false)], null, [], []));
        await database.ApplySchemaAsync(Declare(DatabaseType.JsonBinary), guard.Token);
        SeedRaw(database, [Encoding.UTF8.GetBytes("{}")], 4096);
        long pages = database.DataStorage.GetOwnerPages(Table(database).ObjectId).Count;

        // Act
        await database.ApplySchemaAsync(Declare(targetType), guard.Token);

        // Assert: five-byte binary component becomes six-byte string component.
        database.SchemaProvisioner.JointSpaceReplacementBytes.ShouldBe(((pages * 35 + 33) / 34) * Page.Size);
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - Joint admission: reusable free pages reduce the volume requirement")]
    public async Task ApplyAsync_ReclaimedFreePages_ShouldCreditRetryAllocation()
    {
        // Arrange
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        var storage = new CrashCaptureSqlStorageStrategy();
        await using var engine = await Builder(storage).BuildAsync(guard.Token);
        var database = await engine.CreateDatabaseAsync("typed", guard.Token);
        await database.ApplySchemaAsync(WideCheckpointDeclaration(false), guard.Token);
        SeedRaw(database, [1, new string('x', 4000)], 1024);
        database.SchemaProvisioner.JointStage = stage => { if (stage == "OwnersDurable") { throw new InvalidOperationException("measure admission"); } };
        await Should.ThrowAsync<SqlSchemaMigrationException>(async () => await database.ApplySchemaAsync(WideCheckpointDeclaration(true), guard.Token));
        await DrainReclamationAsync(database, guard.Token);
        long before = database.SchemaProvisioner.JointSpaceNeed;
        await using var session = await database.CreateSessionAsync(guard.Token);
        await session.ExecuteAsync("CREATE TABLE scratch(payload TEXT)", cancellationToken: guard.Token);
        database.Catalog.TryGetTable("dbo", "scratch", out var scratch).ShouldBeTrue();
        byte[] record = SqlRowCodec.Encode(scratch, [new string('x', 7000)], Database.Transactions.TransactionSequence.None);
        for (int start = 0; start < 1024; start += 64)
        {
            using var bracket = database.DataStorage.BeginTransaction();
            for (int row = 0; row < 64; row++) { database.DataStorage.InsertRow(bracket, scratch.ObjectId, record); }
            bracket.Commit();
        }
        await session.ExecuteAsync("DROP TABLE scratch", cancellationToken: guard.Token);
        await DrainReclamationAsync(database, guard.Token);
        database.Coordinator.Checkpoint(guard.Token);
        long reusable = database.DataStorage.PageManager.FreePageCount * Page.Size;
        reusable.ShouldBeGreaterThanOrEqualTo(4L * 1024 * 1024);
        database.SchemaProvisioner.JointStage = null;
        storage.MigrationAvailableBytes = before - 4L * 1024 * 1024;

        // Act
        await database.ApplySchemaAsync(WideCheckpointDeclaration(true), guard.Token);

        // Assert
        database.SchemaProvisioner.JointSpaceNeed.ShouldBeLessThanOrEqualTo(storage.MigrationAvailableBytes);
        database.Catalog.SchemaState!.ContentHash.ShouldBe(WideCheckpointDeclaration(true).Hash);
        database.IsOffline.ShouldBeFalse();
        _output.WriteLine($"retry: previous need={before}; reusable={reusable}; new need={database.SchemaProvisioner.JointSpaceNeed}");
    }
}
