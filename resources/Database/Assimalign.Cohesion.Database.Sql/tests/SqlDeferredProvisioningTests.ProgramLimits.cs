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
    [Fact(DisplayName = "Cohesion Test [Database.Sql] - Deferred index build: oversized stored keys retain the pending intent and size diagnostic (#1266)")]
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
        var offline = new TaskCompletionSource<SqlDatabase>(TaskCreationOptions.RunContinuationsAsynchronously);
        var builder = Builder(storage);
        builder.AddWorker(owner =>
        {
            owner.BeforeProvisioning = database =>
            {
                database.SchemaProvisioner.BeforeDeferredStep = (_, _) => Interlocked.Increment(ref deferredSteps);
                var onOffline = database.DataStorage.OnOffline;
                database.DataStorage.OnOffline = error =>
                {
                    onOffline?.Invoke(error);
                    offline.TrySetResult(database);
                };
            };
            return new RecordingWorker(owner);
        });

        // Act
        await using var engine = await builder.AddDatabase(Schema(true)).BuildAsync(timeout.Token);
        var failed = await offline.Task.WaitAsync(timeout.Token);

        // Assert
        deferredSteps.ShouldBe(1);
        failed.IsOffline.ShouldBeTrue();
        failed.Provisioning.ShouldNotBeNull().FailureCode.ShouldBe("COHSQLP004");
        var migration = failed.OfflineError.ShouldNotBeNull().InnerException.ShouldBeOfType<SqlSchemaMigrationException>();
        migration.InnerException.ShouldBeOfType<SqlEvaluationException>().Code.ShouldBe("COHSQLE042");
        // Offline catalogs refuse reads. Inspect the durable retry image before its next step.
        var image = storage.CaptureDurableImages();
        using var release = new ManualResetEventSlim();
        var retry = Builder(image);
        retry.AddWorker(owner =>
        {
            owner.BeforeProvisioning = database => database.SchemaProvisioner.BeforeDeferredStep = (_, token) => release.Wait(token);
            return new RecordingWorker(owner);
        });
        await using var restarted = await retry.AddDatabase("pending").BuildAsync(timeout.Token);
        var pending = await restarted.OpenDatabaseAsync("pending", timeout.Token);
        pending.Catalog.PendingMigration.ShouldNotBeNull().Steps.ShouldHaveSingleItem().Kind.ShouldBe("AddIndex");
        pending.Catalog.TryGetTable("dbo", "items", out var table).ShouldBeTrue();
        pending.Catalog.GetIndexes(table.ObjectId).ShouldHaveSingleItem().ColumnNames.ShouldBe(["Id"]);
    }

    private sealed record LargeKeyItem(int Id, string? Value);
}
