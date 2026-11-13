using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Graph.Tests.TestObjects;

namespace Assimalign.Cohesion.Database.Graph.Tests;

[Collection(nameof(GraphConcurrencyCollection))]
public sealed class GraphIndexOpenRecoveryTests
{
    [Fact(DisplayName = "Cohesion Test [Graph] - A crash inside open-time detach converges twice to exact surviving owners (#1808)")]
    public async Task OpenDatabaseAsync_DetachCommitImages_ShouldPreserveSurvivingSeek()
    {
        // Arrange.
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        var strategy = new FaultInjectingJournalStorageStrategy(durable: true);
        var options = GraphTextIndexTests.Quiet(); options.StorageStrategy = strategy;
        await using var engine = GraphDatabaseEngine.Create("open-detach", options);
        var database = await engine.CreateDatabaseAsync("source", guard.Token);
        await using var writer = await database.CreateSessionAsync(guard.Token);
        await writer.ExecuteAsync("INSERT (:N {p:'abc',q:1})", cancellationToken: guard.Token);
        await writer.ExecuteAsync("CREATE INDEX kept FOR (n:N) ON (n.q)", cancellationToken: guard.Token);
        var survivors = database.DataStorage.GetIndexOwners(0).ToDictionary(item => item.OwnerId,
            item => database.DataStorage.GetOwnerIndexPages(item.OwnerId).ToArray());
        var registrations = GraphTextIndexTests.Registrations(database).ToArray();
        ulong abandoned = 0;
        database.Store.AfterTextIndexMutation = stage =>
        {
            if (stage != "build-root") { return; }
            abandoned = database.DataStorage.GetIndexOwners(0).Select(item => item.OwnerId).Except(survivors.Keys).Single();
            strategy.CopyDatabase("source", "image");
            throw new InvalidOperationException("injected unregistered definition");
        };
        try { await Should.ThrowAsync<DatabaseException>(async () => await writer.ExecuteAsync("CREATE INDEX lost FOR (n:N) ON (n.p)", cancellationToken: guard.Token)); }
        finally { database.Store.AfterTextIndexMutation = null; }
        int captures = 0;
        FaultInjectingJournalStorageStrategy.BeforeCommitFlush = () => strategy.CopyDatabase("image", "open" + captures++);

        // Act: capture actual detach-bracket commit prefixes during open.
        GraphDatabase opened;
        try { opened = await engine.OpenDatabaseAsync("image", guard.Token); }
        finally { FaultInjectingJournalStorageStrategy.BeforeCommitFlush = null; }
        await opened.DisposeAsync();

        // Assert.
        captures.ShouldBeGreaterThan(0);
        for (int image = -1; image < captures; image++)
        {
            string name = image < 0 ? "image" : "open" + image;
            for (int reopen = 0; reopen < 2; reopen++)
            {
                var recovered = await engine.OpenDatabaseAsync(name, guard.Token);
                for (int pass = 0; pass < 8; pass++) { recovered.Coordinator.RunVersionPurgePass(guard.Token); }
                recovered.DataStorage.GetOwnerIndexPages(abandoned).ShouldBeEmpty();
                recovered.DataStorage.GetIndexOwners(0).Select(item => item.OwnerId).ShouldBe(survivors.Keys.Order());
                foreach (var owner in survivors) { recovered.DataStorage.GetOwnerIndexPages(owner.Key).ShouldBe(owner.Value); }
                GraphTextIndexTests.Registrations(recovered).OrderBy(item => item.Id).ShouldBe(registrations.OrderBy(item => item.Id));
                GraphTextIndexTests.Registrations(recovered).ShouldAllBe(item => item.Root > 0);
                await using (var reader = await recovered.CreateSessionAsync(guard.Token))
                { (await GraphTextIndexTests.RowsAsync(reader, "MATCH (n:N {q:1}) RETURN n.p")).ShouldHaveSingleItem()[0].ShouldBe("abc"); }
                await recovered.DisposeAsync();
            }
        }
    }
}
