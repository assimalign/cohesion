using System;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Sql.Tests.TestObjects;
using Assimalign.Cohesion.Database.Storage;
using Assimalign.Cohesion.Database.Transactions;

namespace Assimalign.Cohesion.Database.Sql.Tests;

public sealed partial class SqlSchemaExecutionTests
{
    [Fact(DisplayName = "Cohesion Test [Sql] - DROP SCHEMA requests reclamation without waiting for a periodic maintenance pass")]
    public async Task ExecuteAsync_Cascade_ShouldRequestAndCompleteReclamation()
    {
        // Arrange: no periodic full pass can rescue a missing session reclamation request.
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var engine = SqlDatabaseEngine.Create("schema-reclamation", new() { MaintenanceInterval = TimeSpan.FromHours(1) });
        var database = await engine.CreateDatabaseAsync("db", timeout.Token);
        await using var session = await database.CreateSessionAsync(timeout.Token);
        database.TableReclamation.RunBatch(timeout.Token);
        while (database.TableReclamation.HasWork) { await Task.Delay(1, timeout.Token); }
        int drops = 0;
        var elapsed = Stopwatch.StartNew();

        // Act: run beyond the minimum until multiple real worker reclamations completed.
        do
        {
            await session.ExecuteAsync("CREATE SCHEMA sales", cancellationToken: timeout.Token);
            await session.ExecuteAsync("CREATE TABLE sales.t(id INT PRIMARY KEY)", cancellationToken: timeout.Token);
            await session.ExecuteAsync("INSERT INTO sales.t VALUES (1)", cancellationToken: timeout.Token);
            var table = database.Catalog.Tables.Single();
            database.DataStorage.GetOwnerPages(table.ObjectId).ShouldNotBeEmpty();
            await session.ExecuteAsync("DROP SCHEMA sales CASCADE", cancellationToken: timeout.Token);
            while (database.TableReclamation.HasWork || database.Catalog.GetPendingTableReclamations().Count != 0)
            {
                await Task.Delay(1, timeout.Token);
            }
            database.DataStorage.GetOwnerPages(table.ObjectId).ShouldBeEmpty();
            database.CatalogStorage.GetOwnerPages(table.ObjectId).ShouldBeEmpty();
            drops++;
        } while (elapsed.Elapsed < TimeSpan.FromMilliseconds(150) || drops < 3);

        // Assert
        drops.ShouldBeGreaterThanOrEqualTo(3);
        database.TableReclamation.ReclaimedPages.ShouldBeGreaterThanOrEqualTo(drops);
        engine.Workers.ShouldAllBe(worker => worker.FailureCount == 0);
        database.IsOffline.ShouldBeFalse();
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - Every DROP SCHEMA crash image recovers either the complete namespace or its complete CASCADE")]
    [InlineData(StorageCommitDurability.Synchronous)]
    [InlineData(StorageCommitDurability.Grouped)]
    [InlineData(StorageCommitDurability.None)]
    public async Task ExecuteAsync_CascadeCrashImages_ShouldRecoverAtomicNamespaceMutation(StorageCommitDurability durability)
    {
        // Arrange: retain an older reader so recording covers publication and directory drops
        // without the independent reclamation worker retiring pages during the capture.
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(1));
        var storage = new CrashCaptureSqlStorageStrategy(durability);
        await using var engine = SqlDatabaseEngine.Create("schema-crashes", new() { StorageStrategy = storage, Durability = durability });
        var database = await engine.CreateDatabaseAsync("db", timeout.Token);
        await using var session = await database.CreateSessionAsync(timeout.Token);
        await SeedAsync(session);
        await using var reader = await database.CreateSessionAsync(timeout.Token);
        await using var transaction = await reader.BeginTransactionAsync(IsolationLevel.Snapshot, timeout.Token);
        ulong generation = database.Catalog.DdlGeneration;
        var retired = database.Catalog.Tables.Where(value => value.Schema == "sales").Select(value => value.ObjectId).ToArray();
        using var recording = storage.RecordCrashPoints();

        // Act
        await session.ExecuteAsync("DROP SCHEMA sales CASCADE", cancellationToken: timeout.Token);
        recording.Dispose();

        // Assert: no image loses only a namespace, table, index registration or incoming FK.
        int before = 0;
        int after = 0;
        for (int point = 0; point < recording.Count; point++)
        {
            await using var reopened = SqlDatabaseEngine.Create("schema-crash-image", new() { StorageStrategy = recording.Open(point), Durability = durability });
            var recovered = await reopened.OpenDatabaseAsync("db", timeout.Token);
            await using var probe = await recovered.CreateSessionAsync(timeout.Token);
            bool exists = recovered.Catalog.Namespaces.Any(value => value.Name == "sales");
            recovered.Catalog.DdlGeneration.ShouldBe(generation + (exists ? 0UL : 1UL));
            recovered.Catalog.Tables.Count.ShouldBe(exists ? 4 : 2);
            recovered.Catalog.TryGetTable("sales", "parent", out _).ShouldBe(exists);
            recovered.Catalog.TryGetTable("sales", "child", out _).ShouldBe(exists);
            recovered.Catalog.TryGetTable("dbo", "external_child", out var child).ShouldBeTrue();
            child.Constraints.Count.ShouldBe(exists ? 1 : 0);
            recovered.Catalog.GetIndexRegistrations().Any(value => retired.Contains(value.ObjectId)).ShouldBe(exists);
            (await RowsAsync(probe, "SELECT pid FROM external_child")).ShouldBe([[1]]);
            if (exists)
            {
                (await RowsAsync(probe, "SELECT id FROM sales.parent")).ShouldBe([[1]]);
                await Should.ThrowAsync<DatabaseException>(() => ExecuteAsync(probe, "INSERT INTO external_child VALUES (99)"));
                before++;
            }
            else
            {
                await ExecuteAsync(probe, "INSERT INTO external_child VALUES (99)");
                await ExecuteAsync(probe, "CREATE SCHEMA sales");
                await ExecuteAsync(probe, "CREATE TABLE sales.parent(id INT GENERATED ALWAYS AS IDENTITY PRIMARY KEY)");
                await ExecuteAsync(probe, "INSERT INTO sales.parent DEFAULT VALUES");
                (await RowsAsync(probe, "SELECT id FROM sales.parent")).ShouldBe([[1]]);
                after++;
            }
            recovered.IsOffline.ShouldBeFalse();
        }
        before.ShouldBeGreaterThan(0);
        after.ShouldBeGreaterThan(0);
    }
}
