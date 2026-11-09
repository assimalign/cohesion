using System;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Database.Documents.Tests;

[Collection("Document index concurrency")]
public sealed class DocumentIndexBuildBudgetTests
{
    [Theory(DisplayName = "Cohesion Test [Documents] - Adjacent array populations pin the 64 KiB build refusal (#1808)")]
    [InlineData(336, false)]
    [InlineData(337, true)]
    public async Task CreateIndexAsync_ArrayGrantBoundary_ShouldRefuseOnlyBeyondMeasuredState(int population, bool refuses)
    {
        // Arrange: one name and one array of distinct integers, beginning at zero.
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        await using var engine = DocumentDatabaseEngine.Create("array-grant-boundary", new()
        { StatementMemoryBudget = 64 * 1024, MaintenanceInterval = TimeSpan.FromHours(1) });
        var database = await engine.CreateDatabaseAsync("test", guard.Token);
        await using var session = await database.CreateSessionAsync(guard.Token);
        var collection = await session.CreateCollectionAsync("items", guard.Token);
        await collection.PutAsync(session, "one", Encoding.UTF8.GetBytes("{\"arr\":[" +
            string.Join(',', Enumerable.Range(0, population)) + "]}"), cancellationToken: guard.Token);

        // Act.
        const string build = "CREATE INDEX ix ON items(DISTINCT ARRAY v FOR v IN arr END)";
        if (refuses)
        {
            var error = await Should.ThrowAsync<DatabaseException>(async () => await session.ExecuteAsync(build, cancellationToken: guard.Token));
            error.Diagnostics.ShouldContain(diagnostic => diagnostic.Code == "CD05018");
        }
        else { await session.ExecuteAsync(build, cancellationToken: guard.Token); }

        // Assert.
        var pin = await database.Coordinator.BeginSnapshotPinAsync(guard.Token);
        try
        {
            var metadata = database.Catalog.FindCollection("items", pin.Snapshot)!.Value;
            database.Catalog.GetIndexes(metadata.Id, pin.Snapshot).Count.ShouldBe(refuses ? 0 : 1);
        }
        finally { await database.Coordinator.RollbackAsync(pin, guard.Token); }
        database.IsOffline.ShouldBeFalse();
    }

    [Fact(DisplayName = "Cohesion Test [Documents] - Unindexed JSON text is charged by payload and token metadata (#1808)")]
    public async Task CreateIndexAsync_LargeUnindexedString_ShouldFitDefaultGrant()
    {
        // Arrange: text bytes do not have one DOM metadata row per byte.
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        await using var engine = DocumentDatabaseEngine.Create("large-text-build", new());
        var database = await engine.CreateDatabaseAsync("test", guard.Token);
        await using var session = await database.CreateSessionAsync(guard.Token);
        var collection = await session.CreateCollectionAsync("items", guard.Token);
        await collection.PutAsync(session, "one", Encoding.UTF8.GetBytes("{\"v\":1,\"padding\":\"" + new string('x', 4 * 1024 * 1024) + "\"}"), cancellationToken: guard.Token);

        // Act.
        await session.ExecuteAsync("CREATE INDEX ix ON items(v)", cancellationToken: guard.Token);

        // Assert.
        database.IsOffline.ShouldBeFalse();
    }

    [Theory(DisplayName = "Cohesion Test [Documents] - Index builds retain a measured name snapshot and one chunk (#1808)")]
    [InlineData(1, 64 * 1024)]
    [InlineData(4096, 64 * 1024 * 1024)]
    [InlineData(250000, 64 * 1024 * 1024)]
    public async Task CreateIndexAsync_SmallDocuments_ShouldFitGrant(int population, long grant)
    {
        // Arrange.
        using var guard = new CancellationTokenSource(TimeSpan.FromMinutes(20));
        await using var engine = DocumentDatabaseEngine.Create("build-budget", new()
        { StatementMemoryBudget = grant });
        var database = await engine.CreateDatabaseAsync("test", guard.Token);
        await using var session = await database.CreateSessionAsync(guard.Token);
        var collection = await session.CreateCollectionAsync("items", guard.Token);
        for (int row = 0; row < population; row++)
        { await collection.PutAsync(session, row.ToString("D10"), Encoding.UTF8.GetBytes($"{{\"v\":{row}}}"), cancellationToken: guard.Token); }

        // Act.
        await session.ExecuteAsync("CREATE UNIQUE INDEX ix ON items(v)", cancellationToken: guard.Token);

        // Assert.
        (await collection.GetAsync(session, "0000000000", guard.Token)).ShouldNotBeNull();
        database.IsOffline.ShouldBeFalse();
    }

    [Fact(DisplayName = "Cohesion Test [Documents] - A ten-thousand-element array build fits the default grant (#1808)")]
    public async Task CreateIndexAsync_LargeArray_ShouldChargeActualKeys()
    {
        // Arrange.
        using var guard = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        await using var engine = DocumentDatabaseEngine.Create("array-build-budget", new());
        var database = await engine.CreateDatabaseAsync("test", guard.Token);
        await using var session = await database.CreateSessionAsync(guard.Token);
        var collection = await session.CreateCollectionAsync("items", guard.Token);
        await collection.PutAsync(session, "one", Encoding.UTF8.GetBytes("{\"arr\":[" +
            string.Join(',', Enumerable.Range(0, 10000).Select(value => $"{{\"p\":{value}}}")) + "]}"), cancellationToken: guard.Token);

        // Act.
        await session.ExecuteAsync("CREATE INDEX ix ON items(DISTINCT ARRAY v.p FOR v IN arr END)", cancellationToken: guard.Token);

        // Assert.
        database.IsOffline.ShouldBeFalse();
        (await collection.GetAsync(session, "one", guard.Token)).ShouldNotBeNull();
    }
}
