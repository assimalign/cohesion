using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Database.Documents.Tests;

[Collection("Document index concurrency")]
public sealed class DocumentIndexRetirementQueueTests
{
    [Fact(DisplayName = "Cohesion Test [Documents] - DROP schedules an owner after live candidates leave retirement (#1808)")]
    public async Task DeleteIndexAsync_ManyLiveIndexes_ShouldRetireDroppedOwnerOnNextPass()
    {
        // Arrange: three passes remove 130 live candidates; requeueing them delayed DROP.
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        await using var engine = DocumentDatabaseEngine.Create("retirement-queue", new() { MaintenanceInterval = TimeSpan.FromHours(1) });
        var database = await engine.CreateDatabaseAsync("test", guard.Token);
        await using var session = await database.CreateSessionAsync(guard.Token);
        var collection = await session.CreateCollectionAsync("items", guard.Token);
        await collection.PutAsync(session, "one", "{\"v\":1}"u8.ToArray(), cancellationToken: guard.Token);
        for (int index = 0; index < 130; index++)
        { await session.ExecuteAsync($"CREATE INDEX ix{index:D3} ON items(v)", cancellationToken: guard.Token); }
        var pin = await database.Coordinator.BeginSnapshotPinAsync(guard.Token);
        Guid collectionId = database.Catalog.FindCollection("items", pin.Snapshot)!.Value.Id;
        var indexes = database.Catalog.GetIndexes(collectionId, pin.Snapshot);
        ulong owner = indexes.Single(index => index.Name == "ix129").ObjectId;
        await database.Coordinator.RollbackAsync(pin, guard.Token);
        for (int pass = 0; pass < 3; pass++) { database.Coordinator.RunVersionPurgePass(guard.Token); }

        // Act.
        await session.ExecuteAsync("DROP INDEX ix129 ON items", cancellationToken: guard.Token);
        database.Coordinator.RunVersionPurgePass(guard.Token);

        // Assert.
        database.DataStorage.GetOwnerIndexPages(owner).ShouldBeEmpty();
        pin = await database.Coordinator.BeginSnapshotPinAsync(guard.Token);
        try
        {
            database.Catalog.GetIndexes(collectionId, pin.Snapshot).Count.ShouldBe(129);
            (await database.Catalog.SearchIndexAsync(collectionId, "ix000", 1m, true, 1m, true, pin.Snapshot, guard.Token)).ShouldHaveSingleItem();
        }
        finally { await database.Coordinator.RollbackAsync(pin, guard.Token); }
    }
}
