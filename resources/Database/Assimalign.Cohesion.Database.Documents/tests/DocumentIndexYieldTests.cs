using System;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Transactions;

namespace Assimalign.Cohesion.Database.Documents.Tests;

[Collection("Document index concurrency")]
public sealed class DocumentIndexYieldTests
{
    [Fact(DisplayName = "Cohesion Test [Documents] - Retirement yields to size and pending checkpoint predicates (#1808)")]
    public async Task RunVersionPurgePass_DueCheckpoint_ShouldYieldThenReclaim()
    {
        // Arrange.
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        await using var engine = DocumentDatabaseEngine.Create("index-yield", new()
        { MaintenanceInterval = TimeSpan.FromHours(1), CheckpointInterval = TimeSpan.FromHours(1) });
        var database = await engine.CreateDatabaseAsync("test", guard.Token);
        await using var session = await database.CreateSessionAsync(guard.Token);
        var collection = await session.CreateCollectionAsync("items", guard.Token);
        await collection.PutAsync(session, "one", "{\"v\":1}"u8.ToArray(), cancellationToken: guard.Token);
        await session.ExecuteAsync("CREATE INDEX ix ON items(v)", cancellationToken: guard.Token);
        ulong owner = database.DataStorage.GetIndexOwners(0).Single().OwnerId;
        var pin = await database.Coordinator.BeginSnapshotPinAsync(guard.Token);
        await session.ExecuteAsync("DROP INDEX ix ON items", cancellationToken: guard.Token);
        database.Coordinator.Checkpoint(guard.Token);
        var pages = database.DataStorage.GetOwnerIndexPages(owner).ToArray();
        var due = database.Catalog.ReclamationCheckpointDue;
        try
        {
            database.Catalog.ReclamationCheckpointDue = () => true;
            await database.Coordinator.RollbackAsync(pin, guard.Token);
            using (var holder = database.DataStorage.BeginTransaction("HoldYieldCheckpoint"))
            {
                database.DataStorage.CheckpointJournalSize = 1;
                database.Catalog.ReclamationCheckpointDue = () => false;

                // Act / Assert: the storage size predicate alone yields before freeing.
                database.DataStorage.IsCheckpointDue(TimeSpan.MaxValue).ShouldBeTrue();
                database.Coordinator.RunVersionPurgePass(guard.Token);
                database.DataStorage.GetOwnerIndexPages(owner).ShouldBe(pages);
            }
            database.DataStorage.CheckpointJournalSize = 0; database.Coordinator.Checkpoint(guard.Token);
            int pendingChecks = 0;
            database.Catalog.ReclamationCheckpointDue = () => { pendingChecks++; return true; };
            database.Coordinator.RunVersionPurgePass(guard.Token);
            pendingChecks.ShouldBeGreaterThan(0);
            database.DataStorage.GetOwnerIndexPages(owner).ShouldBe(pages);
            database.Catalog.ReclamationCheckpointDue = () => false;
            for (int pass = 0; pass < 4; pass++) { database.Coordinator.RunVersionPurgePass(guard.Token); }
            database.DataStorage.GetOwnerIndexPages(owner).ShouldBeEmpty();
        }
        finally { database.DataStorage.CheckpointJournalSize = 0; database.Catalog.ReclamationCheckpointDue = due; }
    }

    [Fact(DisplayName = "Cohesion Test [Documents] - Cancelling after the second committed chunk reclaims live without reopen (#1808)")]
    public async Task CreateIndexAsync_SecondChunkCancellation_ShouldReclaimLiveOwner()
    {
        // Arrange.
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        await using var engine = DocumentDatabaseEngine.Create("live-abandon", new() { MaintenanceInterval = TimeSpan.FromHours(1) });
        var database = await engine.CreateDatabaseAsync("test", guard.Token);
        await using var session = await database.CreateSessionAsync(guard.Token);
        var collection = await session.CreateCollectionAsync("items", guard.Token);
        for (int row = 0; row < 256; row++)
        { await collection.PutAsync(session, row.ToString(), Encoding.UTF8.GetBytes($"{{\"v\":{row}}}"), cancellationToken: guard.Token); }
        using var buildCancellation = CancellationTokenSource.CreateLinkedTokenSource(guard.Token);
        int chunks = 0; ulong owner = 0;
        database.Catalog.AfterIndexRetirementMutation = stage =>
        {
            if (stage != "build-chunk" || ++chunks != 2) { return; }
            owner = database.DataStorage.GetIndexOwners(0).Single().OwnerId;
            buildCancellation.Cancel();
        };

        // Act.
        try { await Should.ThrowAsync<OperationCanceledException>(async () => await session.ExecuteAsync("CREATE INDEX ix ON items(v)", cancellationToken: buildCancellation.Token)); }
        finally { database.Catalog.AfterIndexRetirementMutation = null; }
        for (int pass = 0; pass < 8; pass++) { database.Coordinator.RunVersionPurgePass(guard.Token); }

        // Assert.
        chunks.ShouldBe(2); owner.ShouldBeGreaterThan(1UL);
        database.DataStorage.GetOwnerIndexPages(owner).ShouldBeEmpty();
        using (var registrations = database.DataStorage.GetUnitIterator(1)) { registrations.MoveNext().ShouldBeFalse(); }
        (await collection.GetAsync(session, "255", guard.Token)).ShouldNotBeNull();
    }
}
