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
public sealed class GraphIndexYieldTests
{
    [Fact(DisplayName = "Cohesion Test [Graph] - Retirement yields to size and pending checkpoint predicates (#1808)")]
    public async Task RunVersionPurgePass_DueCheckpoint_ShouldYieldThenReclaim()
    {
        // Arrange.
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        await using var engine = GraphDatabaseEngine.Create("index-yield", GraphTextIndexTests.Quiet());
        var database = await engine.CreateDatabaseAsync("graph", guard.Token);
        await using var session = await database.CreateSessionAsync(guard.Token);
        await session.ExecuteAsync("INSERT (:N {p:'abc'})", cancellationToken: guard.Token);
        await session.ExecuteAsync("CREATE INDEX ix FOR (n:N) ON (n.p)", cancellationToken: guard.Token);
        var pin = await database.Coordinator.BeginSnapshotPinAsync(guard.Token);
        ulong owner = database.Store.GetIndexIdentities(pin.Snapshot).Single().Id;
        await session.ExecuteAsync("DROP INDEX ix", cancellationToken: guard.Token);
        database.Coordinator.Checkpoint(guard.Token);
        var pages = database.DataStorage.GetOwnerIndexPages(owner).ToArray();
        var due = database.Store.ReclamationCheckpointDue;
        try
        {
            database.Store.ReclamationCheckpointDue = () => true;
            await database.Coordinator.RollbackAsync(pin, guard.Token);
            using (var holder = database.DataStorage.BeginTransaction("HoldYieldCheckpoint"))
            {
                database.DataStorage.CheckpointJournalSize = 1;
                database.Store.ReclamationCheckpointDue = () => false;

                // Act / Assert: the size predicate yields without consulting the model signal.
                database.DataStorage.IsCheckpointDue(TimeSpan.MaxValue).ShouldBeTrue();
                database.Coordinator.RunVersionPurgePass(guard.Token);
                database.DataStorage.GetOwnerIndexPages(owner).ShouldBe(pages);
            }
            database.DataStorage.CheckpointJournalSize = 0; database.Coordinator.Checkpoint(guard.Token);
            int pendingChecks = 0;
            database.Store.ReclamationCheckpointDue = () => { pendingChecks++; return true; };
            database.Coordinator.RunVersionPurgePass(guard.Token);
            pendingChecks.ShouldBeGreaterThan(0);
            database.DataStorage.GetOwnerIndexPages(owner).ShouldBe(pages);
            database.Store.ReclamationCheckpointDue = () => false;
            for (int pass = 0; pass < 4; pass++) { database.Coordinator.RunVersionPurgePass(guard.Token); }
            database.DataStorage.GetOwnerIndexPages(owner).ShouldBeEmpty();
        }
        finally { database.DataStorage.CheckpointJournalSize = 0; database.Store.ReclamationCheckpointDue = due; }
    }

    [Theory(DisplayName = "Cohesion Test [Graph] - Exact and text builds cancelled at chunk two reclaim live without reopen (#1808)")]
    [InlineData(false)] [InlineData(true)]
    public async Task CreateIndexAsync_SecondChunkCancellation_ShouldReclaimLiveOwner(bool text)
    {
        // Arrange.
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        await using var engine = GraphDatabaseEngine.Create("live-abandon", GraphTextIndexTests.Quiet());
        var database = await engine.CreateDatabaseAsync("graph", guard.Token);
        await using var session = await database.CreateSessionAsync(guard.Token);
        await session.ExecuteAsync("INSERT (:N {p:'survive',q:1})", cancellationToken: guard.Token);
        await GraphTextIndexReviewTests.PopulateAsync(session, 130, "abcdefghij");
        await session.ExecuteAsync("CREATE INDEX kept FOR (n:N) ON (n.q)", cancellationToken: guard.Token);
        var owners = database.DataStorage.GetIndexOwners(0).Select(item => item.OwnerId).ToArray();
        var registrations = GraphTextIndexTests.Registrations(database).ToArray();
        using var buildCancellation = CancellationTokenSource.CreateLinkedTokenSource(guard.Token);
        int chunks = 0; ulong owner = 0;
        database.Store.AfterTextIndexMutation = stage =>
        {
            if (stage != "build-chunk" || ++chunks != 2) { return; }
            owner = database.DataStorage.GetIndexOwners(0).Select(item => item.OwnerId).Except(owners).Single();
            buildCancellation.Cancel();
        };

        // Act.
        try { await Should.ThrowAsync<OperationCanceledException>(async () => await session.ExecuteAsync($"CREATE {(text ? "TEXT " : "")}INDEX lost FOR (n:N) ON (n.p)", cancellationToken: buildCancellation.Token)); }
        finally { database.Store.AfterTextIndexMutation = null; }
        for (int pass = 0; pass < 16; pass++) { database.Coordinator.RunVersionPurgePass(guard.Token); }

        // Assert.
        chunks.ShouldBe(2); owner.ShouldBeGreaterThan(2UL);
        database.DataStorage.GetOwnerIndexPages(owner).ShouldBeEmpty();
        database.DataStorage.GetIndexOwners(0).Select(item => item.OwnerId).ShouldBe(owners);
        GraphTextIndexTests.Registrations(database).OrderBy(item => item.Id).ShouldBe(registrations.OrderBy(item => item.Id));
        (await GraphTextIndexTests.RowsAsync(session, "MATCH (n:N {q:1}) RETURN n.p")).ShouldHaveSingleItem()[0].ShouldBe("survive");
    }
}
