using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Sql.Internal;
using Assimalign.Cohesion.Database.Sql.Schema;
using Assimalign.Cohesion.Database.Sql.Tests.TestObjects;
using Assimalign.Cohesion.Database.Storage.Units;
using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Sql.Tests;

public sealed partial class SqlJointTypeMigrationTests
{
    [Theory(DisplayName = "Cohesion Test [Sql] - Safe rollback index build publishes once or preserves the old schema and reclaims its owner (#1779, #1808)")]
    [InlineData(false, 65536)]
    [InlineData(true, 65536)]
    [InlineData(false, 1048576)]
    [InlineData(true, 1048576)]
    public async Task ApplySchemaAsync_RollbackIndexWorkspace_ShouldPublishAllOrNothing(bool deferred, long budget)
    {
        // Arrange: the bounded INT key is Safe, but each build chunk retains full rows.
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        SqlCompiledSchema Declaration(bool indexed) => Compiled(new CompiledSchemaTable("items", "row",
            [new("Value", DatabaseType.Int32, false), new("Payload", DatabaseType.String, false)], null,
            indexed ? [new("restored", ["Value"], false)] : [new("survivor", ["Value"], false)], []));
        var first = Declaration(true);
        var second = Declaration(false);
        var storage = new CrashCaptureSqlStorageStrategy();
        await using (var seed = await Builder(storage).BuildAsync(guard.Token))
        {
            var database = await seed.CreateDatabaseAsync("typed", guard.Token);
            await database.ApplySchemaAsync(first, guard.Token);
            await database.ApplySchemaAsync(second, guard.Token);
            await using var writer = await database.CreateSessionAsync(guard.Token);
            string payload = new('x', 2048);
            for (int chunk = 0; chunk < 4; chunk++)
            {
                await writer.ExecuteAsync("INSERT INTO items VALUES" + string.Join(',', Enumerable.Repeat($"(1,'{payload}')", 64)), cancellationToken: guard.Token);
            }
            await DrainReclamationAsync(database, guard.Token);
            storage = storage.CaptureDurableImages();
        }
        var builder = Builder(storage);
        builder.Options.StatementMemoryBudget = budget;
        await using var engine = await builder.BuildAsync(guard.Token);
        var current = await engine.OpenDatabaseAsync("typed", guard.Token);
        var oldTable = Table(current);
        var oldRegistration = current.IndexManager.ExportRegistrations().Single();
        ulong oldOwner = current.IndexManager.PageOwner(oldRegistration);
        var oldPages = current.DataStorage.GetOwnerIndexPages(oldOwner).ToArray();
        var heapPages = current.DataStorage.GetOwnerPages(oldTable.ObjectId).ToArray();
        int publications = 0;
        current.SchemaProvisioner.JointStage = phase => { if (phase == "Publishing") { publications++; } };
        bool fits = budget == 1048576;

        // Act.
        if (deferred)
        {
            await current.SchemaProvisioner.ApplyAsync(first, guard.Token, deferDataScalingSteps: true);
            await WaitDoneAsync(current, guard.Token);
            if (!fits) { current.Provisioning.ShouldNotBeNull().FailureCode.ShouldBe("CS03402"); }
        }
        else if (fits) { await current.ApplySchemaAsync(first, guard.Token); }
        else
        {
            var failure = await Should.ThrowAsync<SqlSchemaMigrationException>(async () => await current.ApplySchemaAsync(first, guard.Token));
            _output.WriteLine(failure.ToString());
            failure.Diagnostics.ShouldContain(d => d.Code == "CS03402");
            Exception? cause = failure;
            bool memoryRefused = false;
            while (cause is not null)
            {
                if (cause is DatabaseException coded) { memoryRefused |= coded.Diagnostics.Any(d => d.Code == "CS05067"); }
                cause = cause.InnerException;
            }
            memoryRefused.ShouldBeTrue();
        }

        // Assert: both routes keep the heap and clean every abandoned owner.
        await DrainReclamationAsync(current, guard.Token);
        current.IsOffline.ShouldBeFalse();
        Table(current).ObjectId.ShouldBe(oldTable.ObjectId);
        current.DataStorage.GetOwnerPages(oldTable.ObjectId).ShouldBe(heapPages);
        current.Catalog.PendingMigration.ShouldBeNull();
        current.Catalog.GetPendingTableReclamations().ShouldBeEmpty();
        var liveOwners = current.IndexManager.ExportRegistrations().Select(current.IndexManager.PageOwner).ToHashSet();
        for (long id = 1; id < current.DataStorage.Data.Length / Page.Size; id++)
        {
            if (!current.DataStorage.FreeSpaceMap.IsAllocated((Assimalign.Cohesion.Database.Storage.PageId)id)) { continue; }
            using var page = current.DataStorage.PageManager.GetPage((Assimalign.Cohesion.Database.Storage.PageId)id);
            if (page.Page.Type == Assimalign.Cohesion.Database.Storage.PageType.Index) { liveOwners.ShouldContain(page.Page.OwnerId); }
        }
        current.Catalog.AppliedSchemaHistory.Length.ShouldBe(2);
        current.Catalog.SchemaState!.ContentHash.ShouldBe(fits ? first.Hash : second.Hash);
        current.Catalog.GetIndexes(oldTable.ObjectId).Select(i => i.Name).ShouldBe([fits ? "restored" : "survivor"]);
        publications.ShouldBe(fits ? 1 : 0);
        current.SchemaProvisioner.JointMetrics.ShouldNotBeNull().SelfCommits.ShouldBe(fits ? 1 : 0);
        if (!fits) { current.DataStorage.GetOwnerIndexPages(oldOwner).ShouldBe(oldPages); }
        await using var reader = await current.CreateSessionAsync(guard.Token);
        (await RowsAsync(reader, "SELECT COUNT(*) FROM items WHERE Value=1", guard.Token)).Single()[0].ShouldBe(256L);
        await using var reopened = await Builder(storage.CaptureDurableImages()).BuildAsync(guard.Token);
        var image = await reopened.OpenDatabaseAsync("typed", guard.Token);
        image.Catalog.SchemaState!.ContentHash.ShouldBe(fits ? first.Hash : second.Hash);
        image.Catalog.GetPendingTableReclamations().ShouldBeEmpty();
    }
}
