using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.ApplicationModel.Hosting;
using Assimalign.Cohesion.Database.Graph;
using Assimalign.Cohesion.Database.Graph.Storage;
using Assimalign.Cohesion.Database.Sql;
using Assimalign.Cohesion.Database.Sql.Catalog;
using Assimalign.Cohesion.Database.Sql.Storage;

namespace Assimalign.Cohesion.Database.Hosting.ApplicationModel.Tests;

[Collection(DatabaseHealthCollection.Name)]
public sealed class DatabaseFormatRefusalHealthTests
{
    [Theory(DisplayName = "Cohesion Test [Database.Hosting.ApplicationModel] - Format fences remain unhealthy without health reopening or changing files (#1790, #1806)")]
    [InlineData(false, "CS06041")]
    [InlineData(true, "CG06019")]
    public async Task CheckAsync_RefusedStoredFormat_ShouldRetainFailureAndEveryByte(bool graph, string code)
    {
        // Arrange: reproduce the model suites' actual catalog images through storage transactions.
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        using var directory = new TemporaryDirectory();
        if (graph)
        {
            await using (var creator = GraphDatabaseEngine.Create("format", new() { RootPath = directory.Path }))
            {
                var database = await creator.CreateDatabaseAsync("stored", guard.Token);
                await using var session = await database.CreateSessionAsync(guard.Token);
                await session.ExecuteAsync("INSERT (:N {p:1})", cancellationToken: guard.Token);
            }
            string root = Path.Combine(directory.Path, "stored");
            using var storage = GraphStorage.Open(File.Open(Path.Combine(root, "graph.dat"), FileMode.Open, FileAccess.ReadWrite),
                File.Open(Path.Combine(root, "graph.log"), FileMode.Open, FileAccess.ReadWrite),
                File.Open(Path.Combine(root, "graph.bak"), FileMode.Open, FileAccess.ReadWrite), checkpointOnOpen: false);
            using var records = storage.GetUnitIterator(0);
            records.MoveNext().ShouldBeTrue();
            var record = records.Current;
            byte[] bytes = record.Data.ToArray();
            bytes[17].ShouldBe((byte)5); bytes[17] = 4;
            using var bracket = storage.BeginTransaction();
            storage.UpdateEntry(bracket, record.PageId, record.SlotIndex, bytes); bracket.Commit();
        }
        else
        {
            await using var creator = SqlDatabaseEngine.Create("format", new() { RootPath = directory.Path });
            var database = await creator.CreateDatabaseAsync("stored", guard.Token);
            await using var session = await database.CreateSessionAsync(guard.Token);
            await session.ExecuteAsync("CREATE TABLE t(id INT PRIMARY KEY)", cancellationToken: guard.Token);
            await session.ExecuteAsync("INSERT INTO t VALUES(1)", cancellationToken: guard.Token);
            await session.DisposeAsync();
            await creator.DisposeAsync();
            string root = Path.Combine(directory.Path, "stored");
            using var storage = SqlStorage.Open(File.Open(Path.Combine(root, "catalog.dat"), FileMode.Open, FileAccess.ReadWrite),
                File.Open(Path.Combine(root, "catalog.log"), FileMode.Open, FileAccess.ReadWrite),
                File.Open(Path.Combine(root, "catalog.bak"), FileMode.Open, FileAccess.ReadWrite), checkpointOnOpen: false);
            await SqlCatalog.Open(storage).SetRecordSpaceFormatVersionAsync(37, guard.Token);
        }
        var before = Directory.GetFiles(directory.Path, "*", SearchOption.AllDirectories)
            .ToDictionary(path => path, File.ReadAllBytes);
        await using DatabaseEngine engine = graph
            ? GraphDatabaseEngine.Create("format", new() { RootPath = directory.Path })
            : SqlDatabaseEngine.Create("format", new() { RootPath = directory.Path });
        var builder = new DatabaseApplicationBuilder(new DatabaseApplicationOptions { ReopenOfflineDatabases = false });
        builder.AddEngine(engine);
        await using var application = builder.Build(guard.Token);
        var resource = new Internal.DatabaseResource("format-health", null, [], application.Context);
        resource.HealthContributors.Select(value => value.Name).ShouldNotContain("database/format/stored");

        // Act: the only explicit open fails once; repeated discovery and evaluation cannot retry it.
        var failure = await Should.ThrowAsync<DatabaseException>(async () => await engine.OpenDatabaseAsync("stored", guard.Token));
        var recordFailure = engine.FailedOpens.ShouldHaveSingleItem();
        var contributor = resource.HealthContributors.Single(value => value.Name == "database/format/stored");
        for (int pass = 0; pass < 3; pass++)
        {
            var contribution = await contributor.CheckAsync(guard.Token);
            var observation = await application.Context.GetHealthAsync(contributor.Name, guard.Token);
            var inventory = await application.Context.GetHealthAsync(guard.Token);

            // Assert: snapshots retain safe structured metadata, one component and one failed attempt.
            failure.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe(code);
            contribution.Status.ShouldBe(ApplicationInstanceHealthStatus.Unhealthy);
            contribution.Data.ShouldBe(observation.Data);
            contribution.Data!["failureCode"].ShouldBe(code);
            contribution.Data!["reopenFault"].ShouldBe(failure.GetType().Name);
            contribution.Data!["reopenAttempts"].ShouldBe("1");
            contribution.Data!["failedAt"].ShouldBe(recordFailure.FailedAt.ToString("O", CultureInfo.InvariantCulture));
            contribution.Description!.ShouldNotContain(failure.Message, Case.Sensitive);
            inventory.Count(value => value.Name == contributor.Name).ShouldBe(1);
            var engineHealth = inventory.Single(value => value.Name == "engine/format");
            engineHealth.Data["failedOpenDatabaseCount"].ShouldBe("1");
            engineHealth.Data.Keys.ShouldNotContain("failedOpen.0.failureCode");
            engine.FailedOpens.ShouldHaveSingleItem().ShouldBe(recordFailure);
            engine.OpenDatabases.ShouldBeEmpty();
            AssertFiles();
        }
        await engine.DisposeAsync();
        engine.FailedOpens.ShouldBeEmpty();
        AssertFiles();

        void AssertFiles()
        {
            Directory.GetFiles(directory.Path, "*", SearchOption.AllDirectories).Order().ShouldBe(before.Keys.Order());
            foreach (var (file, bytes) in before) { File.ReadAllBytes(file).ShouldBe(bytes); }
        }
    }
}
