using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Graph.Tests.TestObjects;
using Assimalign.Cohesion.Database.Transactions;

namespace Assimalign.Cohesion.Database.Graph.Tests;

[Collection(nameof(GraphConcurrencyCollection))]
public sealed class GraphDirectIndexBuildTests
{
    [Fact(DisplayName = "Cohesion Test [Graph] - Direct index builds retain caller accounting beyond one million visits (#1808)")]
    public async Task CreateIndexAsync_DirectLargeBuild_ShouldRetainBaseAvailability()
    {
        // Arrange: exact backfill visits each node twice; this exceeded the fixed cap.
        using var guard = new CancellationTokenSource(TimeSpan.FromMinutes(10));
        const int nodes = 600000;
        await using var engine = GraphDatabaseEngine.Create("direct-build", new());
        var database = await engine.CreateDatabaseAsync("graph", guard.Token);
        await using var session = await database.CreateSessionAsync(guard.Token);
        await GraphTextIndexReviewTests.PopulateAsync(session, nodes, "value");
        var context = await database.Coordinator.BeginAsync(IsolationLevel.Snapshot, guard.Token);
        ulong owner;

        // Act.
        try
        {
            await database.Store.CreateIndexAsync("N", "p", context, guard.Token);
            owner = database.Store.GetIndexIdentities(context.Snapshot).Single().Id;
            await database.Coordinator.CommitAsync(context, guard.Token);
        }
        catch { await database.Coordinator.RollbackAsync(context, CancellationToken.None); throw; }

        // Assert.
        (await GraphTextIndexTests.CountEntriesAsync(database, owner)).ShouldBe(nodes);
        database.DataStorage.GetOwnerIndexPageCount(owner).ShouldBeGreaterThan(0);
        database.IsOffline.ShouldBeFalse();
    }
}
