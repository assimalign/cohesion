using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Sql.Catalog;
using Assimalign.Cohesion.Database.Sql.Internal;
using Assimalign.Cohesion.Database.Sql.Schema;
using Assimalign.Cohesion.Database.Sql.Tests.TestObjects;

namespace Assimalign.Cohesion.Database.Sql.Tests;

public sealed partial class SqlJointTypeMigrationTests
{
    [Fact(DisplayName = "Cohesion Test [Sql] - Joint space admission: a real file-backed volume supports TYPE publication")]
    public async Task ApplyAsync_FileBackedVolume_ShouldAdmitAndPublish()
    {
        // Arrange
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        string root = Path.Combine(Path.GetTempPath(), "cohesion-joint-space-" + Guid.NewGuid().ToString("N"));
        try
        {
            await using var engine = SqlDatabaseEngine.Create("joint-file-volume", new() { RootPath = root });
            var database = await engine.CreateDatabaseAsync("typed", guard.Token);
            await database.ApplySchemaAsync(Source().Compile(), guard.Token);
            await using var writer = await database.CreateSessionAsync(guard.Token);
            await writer.ExecuteAsync("INSERT INTO items VALUES(1,9)", cancellationToken: guard.Token);

            // Act
            await database.ApplySchemaAsync(Target().Compile(), guard.Token);

            // Assert
            database.Catalog.SchemaState!.ContentHash.ShouldBe(Target().Compile().Hash);
            database.Catalog.PendingMigration.ShouldBeNull();
            database.OfflineError.ShouldBeNull();
            (await RowsAsync(writer, "SELECT Value FROM items", guard.Token)).ShouldHaveSingleItem()[0].ShouldBe(9L);
        }
        finally { if (Directory.Exists(root)) { Directory.Delete(root, recursive: true); } }
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - Joint space admission: a small volume refuses before a new or resumed intent write")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ApplyAsync_InsufficientSpace_ShouldRefuseOnlineBeforeAnyDurableWrite(bool keptIntent)
    {
        // Arrange
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var storage = await SeedAsync(guard.Token);
        await using var engine = await Builder(storage).BuildAsync(guard.Token);
        var database = await engine.OpenDatabaseAsync("typed", guard.Token);
        var target = Target().Compile();
        if (keptIntent)
        {
            await database.Catalog.SavePendingMigrationAsync(new(target.Hash, target.CanonicalDocument,
                [new("Joint/AlterColumn", "items", "Value", 1)], 1), guard.Token);
        }
        var intent = database.Catalog.PendingMigration;
        int writes = 0, decoded = 0;
        storage.MigrationAvailableBytes = 0;
        storage.StorageWriteStarting = _ => Interlocked.Increment(ref writes);
        SqlPlanExecutor.RowDecoded = _ => Interlocked.Increment(ref decoded);
        try
        {
            // Act
            var failure = await Should.ThrowAsync<SqlSchemaMigrationException>(async () =>
                await database.SchemaProvisioner.ApplyAsync(target, guard.Token, true));
            // Assert
            failure.Diagnostics.ShouldContain(diagnostic => diagnostic.Code == "CS03402");
            failure.Message.ShouldContain("bytes needed");
            failure.Message.ShouldContain("0 bytes available");
            writes.ShouldBe(0);
            decoded.ShouldBe(0);
            database.Catalog.PendingMigration.ShouldBeSameAs(intent);
            database.Catalog.SchemaState!.ContentHash.ShouldBe(Source().Compile().Hash);
            database.Provisioning!.FailureCode.ShouldBe("CS03402");
            database.OfflineError.ShouldBeNull();
        }
        finally { storage.StorageWriteStarting = null; SqlPlanExecutor.RowDecoded = null; }
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - Joint space admission: a kept crash intent is rechecked during the matching Build")]
    public async Task BuildAsync_KeptIntentOnFullVolume_ShouldRefuseOnlineAndPermitOldBuild()
    {
        // Arrange
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var storage = await SeedAsync(guard.Token);
        await using (var seed = await Builder(storage).BuildAsync(guard.Token))
        {
            var database = await seed.OpenDatabaseAsync("typed", guard.Token);
            var target = Target().Compile();
            await database.Catalog.SavePendingMigrationAsync(new(target.Hash, target.CanonicalDocument,
                [new("Joint/AlterColumn", "items", "Value", 1)], 1), guard.Token);
            storage = storage.CaptureDurableImages();
        }
        storage.MigrationAvailableBytes = 0;
        SqlDatabase? observed = null;
        var builder = Builder(storage);
        builder.AddWorker(owner => { owner.BeforeProvisioning = database => observed = database; return new RecordingWorker(owner); });
        // Act
        var failure = await Should.ThrowAsync<SqlSchemaMigrationException>(async () => await builder.AddDatabase(Target()).BuildAsync(guard.Token));
        // Assert
        failure.Diagnostics.ShouldContain(diagnostic => diagnostic.Code == "CS03402");
        observed.ShouldNotBeNull().IsOffline.ShouldBeFalse();
        await using var rollback = await Builder(storage.CaptureDurableImages()).AddDatabase(Source()).BuildAsync(guard.Token);
        var old = await rollback.OpenDatabaseAsync("typed", guard.Token);
        old.Catalog.SchemaState!.ContentHash.ShouldBe(Source().Compile().Hash);
        old.Catalog.PendingMigration.ShouldBeNull();
    }
}
