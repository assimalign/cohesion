using System;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Sql.Internal;
using Assimalign.Cohesion.Database.Sql.Schema;
using Assimalign.Cohesion.Database.Sql.Tests.TestObjects;

namespace Assimalign.Cohesion.Database.Sql.Tests;

public sealed partial class SqlDeferredProvisioningTests
{
    [Fact(DisplayName = "Cohesion Test [Database.Sql] - Deferred index build: oversized stored keys discard preparation online with the size diagnostic (#1266)")]
    public async Task BuildAsync_DeferredOversizedIndexKey_ShouldRefusePublicationWithSizeDiagnostic()
    {
        // Arrange
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        SqlSchema Schema(bool index) => SqlSchema.Create("pending", schema =>
        {
            schema.Table<LargeKeyItem>("items", table =>
            {
                table.Key(row => row.Id);
                table.Column(row => row.Value);
                if (index) { table.Index(row => row.Value); }
            });
        });
        var storage = new CrashCaptureSqlStorageStrategy();
        await using (var seed = await Builder(storage).BuildAsync(timeout.Token))
        {
            var database = await seed.CreateDatabaseAsync("pending", timeout.Token);
            await database.ApplySchemaAsync(Schema(false).Compile(), timeout.Token);
            await using var session = await database.CreateSessionAsync(timeout.Token);
            await session.ExecuteAsync($"INSERT INTO items VALUES (1, '{new string('x', 2000)}')", cancellationToken: timeout.Token);
            storage = storage.CaptureDurableImages();
        }
        int deferredSteps = 0;
        Exception? refusal = null;
        var builder = Builder(storage);
        builder.AddWorker(owner =>
        {
            owner.BeforeProvisioning = database =>
            {
                database.SchemaProvisioner.BeforeDeferredStep = (_, _) => Interlocked.Increment(ref deferredSteps);
                database.SchemaProvisioner.DeferredFailed = failure => refusal = failure;
            };
            return new RecordingWorker(owner);
        });

        // Act
        await using var engine = await builder.AddDatabase(Schema(true)).BuildAsync(timeout.Token);
        var failed = await engine.OpenDatabaseAsync("pending", timeout.Token);
        await WaitDoneAsync(failed, timeout.Token);

        // Assert
        deferredSteps.ShouldBe(1);
        failed.IsOffline.ShouldBeFalse();
        failed.OfflineError.ShouldBeNull();
        failed.Provisioning.ShouldNotBeNull().FailureCode.ShouldBe("CS03402");
        failed.Catalog.PendingMigration.ShouldBeNull();
        failed.Catalog.TryGetTable("dbo", "items", out var table).ShouldBeTrue();
        failed.Catalog.GetIndexes(table.ObjectId).ShouldHaveSingleItem().ColumnNames.ShouldBe(["Id"]);
        var migration = refusal.ShouldNotBeNull().ShouldBeOfType<SqlSchemaMigrationException>();
        migration.Diagnostics.ShouldContain(diagnostic => diagnostic.Code == "CS03402");
        migration.InnerException.ShouldNotBeNull().InnerException.ShouldBeOfType<SqlEvaluationException>().Code.ShouldBe("CS05042");
        await using var restarted = await Builder(storage.CaptureDurableImages()).AddDatabase(Schema(false)).BuildAsync(timeout.Token);
        var pending = await restarted.OpenDatabaseAsync("pending", timeout.Token);
        pending.Catalog.PendingMigration.ShouldBeNull();
        pending.Catalog.SchemaState!.ContentHash.ShouldBe(Schema(false).Compile().Hash);
    }

    private sealed record LargeKeyItem(int Id, string? Value);
}
