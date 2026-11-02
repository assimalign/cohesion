using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Sql.Schema;
using Assimalign.Cohesion.Database.Sql.Tests.TestObjects;
using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Sql.Tests;

public sealed partial class SqlJointTypeMigrationTests
{
    [Theory(DisplayName = "Cohesion Test [Sql] - Joint TYPE: failed and interrupted preparation permits old-application Build and a later new Build")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BuildAsync_RollbackBeforePublication_ShouldOpenOldAndRetryNew(bool crash)
    {
        // Arrange
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var storage = await SeedAsync(timeout.Token);
        CrashCaptureSqlStorageStrategy? image = null;
        await using (var engine = await Builder(storage).BuildAsync(timeout.Token))
        {
            var database = await engine.OpenDatabaseAsync("typed", timeout.Token);
            database.SchemaProvisioner.JointStage = phase =>
            {
                if (phase != "TablePrepared") { return; }
                if (crash) { image = storage.CaptureDurableImages(); }
                throw new InvalidOperationException("injected unpublished failure");
            };
            await Should.ThrowAsync<SqlSchemaMigrationException>(async () => await database.ApplySchemaAsync(Target().Compile(), timeout.Token));
            image ??= storage.CaptureDurableImages();
        }

        if (crash)
        {
            await using var sameTarget = await Builder(image!.CaptureDurableImages()).AddDatabase(Target()).BuildAsync(timeout.Token);
            var resumed = await sameTarget.OpenDatabaseAsync("typed", timeout.Token);
            await WaitDoneAsync(resumed, timeout.Token);
            resumed.Catalog.SchemaState!.ContentHash.ShouldBe(Target().Compile().Hash);
            resumed.Catalog.PendingMigration.ShouldBeNull();
        }

        // Act: the rolled-back application declares the original schema.
        await using (var rolledBack = await Builder(image!).AddDatabase(Source()).BuildAsync(timeout.Token))
        {
            var database = await rolledBack.OpenDatabaseAsync("typed", timeout.Token);

            // Assert
            database.SchemaProvisioner.Deferred.ShouldBeNull();
            database.Catalog.PendingMigration.ShouldBeNull();
            database.Catalog.SchemaState!.ContentHash.ShouldBe(Source().Compile().Hash);
            database.Provisioning.ShouldBeNull();
            await using var session = await database.CreateSessionAsync(timeout.Token);
            await session.ExecuteAsync("INSERT INTO items VALUES(1000,9)", cancellationToken: timeout.Token);
            await session.ExecuteAsync("INSERT INTO other VALUES(10)", cancellationToken: timeout.Token);
            image = image!.CaptureDurableImages();
        }
        await using var retried = await Builder(image!).AddDatabase(Target()).BuildAsync(timeout.Token);
        var migrated = await retried.OpenDatabaseAsync("typed", timeout.Token);
        await WaitDoneAsync(migrated, timeout.Token);
        migrated.Catalog.SchemaState!.ContentHash.ShouldBe(Target().Compile().Hash);
        await using var reader = await migrated.CreateSessionAsync(timeout.Token);
        (await RowsAsync(reader, "SELECT Value FROM items WHERE Id=1000", timeout.Token)).Single()[0].ShouldBe(9L);
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - Joint TYPE: reverse Build gates lossy conversion before any step and allows exact widening")]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task BuildAsync_ReverseType_ShouldRequireOptInOnlyForLossyConversion(bool lossy, bool optIn)
    {
        // Arrange
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        SqlSchema Old(bool allow) => SqlSchema.Create("typed", schema =>
        {
            if (allow) { schema.AllowDestructiveChanges(); }
            if (lossy) { schema.Table<SmallRollbackRow>("items", table => table.Column(row => row.Value)); }
            else { schema.Table<LargeRollbackRow>("items", table => table.Column(row => row.Value)); }
        });
        SqlSchema New() => SqlSchema.Create("typed", schema =>
        {
            schema.AllowDestructiveChanges();
            if (lossy) { schema.Table<LargeRollbackRow>("items", table => table.Column(row => row.Value)); }
            else { schema.Table<SmallRollbackRow>("items", table => table.Column(row => row.Value)); }
        });
        var storage = new CrashCaptureSqlStorageStrategy();
        await using (var seed = await Builder(storage).BuildAsync(timeout.Token))
        {
            var database = await seed.CreateDatabaseAsync("typed", timeout.Token);
            await database.ApplySchemaAsync(Old(true).Compile(), timeout.Token);
            await using var writer = await database.CreateSessionAsync(timeout.Token);
            await writer.ExecuteAsync("INSERT INTO items VALUES(7)", cancellationToken: timeout.Token);
            await database.ApplySchemaAsync(New().Compile(), timeout.Token);
            storage = storage.CaptureDurableImages();
        }

        // Act / Assert
        if (lossy && !optIn)
        {
            var failure = await Should.ThrowAsync<SqlSchemaMigrationException>(async () =>
                await Builder(storage).AddDatabase(Old(false)).BuildAsync(timeout.Token));
            failure.Message.ShouldContain("CS03105");
            await using var unchanged = await Builder(storage).BuildAsync(timeout.Token);
            var database = await unchanged.OpenDatabaseAsync("typed", timeout.Token);
            database.Catalog.SchemaState!.ContentHash.ShouldBe(New().Compile().Hash);
            database.Catalog.PendingMigration.ShouldBeNull();
            Table(database).FindColumn("Value")!.Type.Type.ShouldBe(DatabaseType.Int32);
        }
        else
        {
            await using var reversed = await Builder(storage).AddDatabase(Old(optIn)).BuildAsync(timeout.Token);
            var database = await reversed.OpenDatabaseAsync("typed", timeout.Token);
            await WaitDoneAsync(database, timeout.Token);
            database.Catalog.SchemaState!.ContentHash.ShouldBe(Old(optIn).Compile().Hash);
            await using var reader = await database.CreateSessionAsync(timeout.Token);
            Convert.ToInt32((await RowsAsync(reader, "SELECT Value FROM items", timeout.Token)).Single()[0]).ShouldBe(7);
        }
    }

    private sealed record SmallRollbackRow(short Value);
    private sealed record LargeRollbackRow(int Value);
}
