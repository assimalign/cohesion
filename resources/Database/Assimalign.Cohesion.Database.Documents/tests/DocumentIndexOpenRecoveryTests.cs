using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Storage;
using Assimalign.Cohesion.Database.Transactions;

namespace Assimalign.Cohesion.Database.Documents.Tests;

[Collection("Document index concurrency")]
public sealed class DocumentIndexOpenRecoveryTests
{
    [Theory(DisplayName = "Cohesion Test [Documents] - Orphan receipts and open-time detach commits converge twice (#1808)")]
    [InlineData("orphan")]
    [InlineData("detach")]
    [InlineData("drop")]
    public async Task OpenDatabaseAsync_InterruptedRetirement_ShouldKeepExactSurvivingOwner(string kind)
    {
        // Arrange.
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        var strategy = new FaultInjectingJournalStorageStrategy(durable: true);
        await using var engine = DocumentDatabaseEngine.Create("open-receipts", new()
        { StorageStrategy = strategy, MaintenanceInterval = TimeSpan.FromHours(1), CheckpointInterval = TimeSpan.FromHours(1) });
        var database = await engine.CreateDatabaseAsync("source", guard.Token);
        await using var writer = await database.CreateSessionAsync(guard.Token);
        var collection = await writer.CreateCollectionAsync("items", guard.Token);
        await collection.PutAsync(writer, "one", "{\"v\":1}"u8.ToArray(), cancellationToken: guard.Token);
        await writer.ExecuteAsync("CREATE INDEX kept ON items(v)", cancellationToken: guard.Token);
        var kept = database.DataStorage.GetIndexOwners(0).ShouldHaveSingleItem();
        var keptPages = database.DataStorage.GetOwnerIndexPages(kept.OwnerId).ToArray();
        var metadataPin = await database.Coordinator.BeginSnapshotPinAsync(guard.Token);
        Guid collectionId = database.Catalog.FindCollection("items", metadataPin.Snapshot)!.Value.Id;
        await database.Coordinator.RollbackAsync(metadataPin, guard.Token);
        ulong abandoned = 0;
        if (kind == "orphan")
        {
            abandoned = (ulong)database.DataStorage.ReserveTransactionSequence();
            using (var admission = TransactionCheckpointInterlock.ForStorage(database.DataStorage).Enter("SeedOrphan", cancellationToken: guard.Token))
            using (var bracket = database.DataStorage.BeginTransaction())
            {
                using var page = database.DataStorage.AllocatePageForWrite(bracket, PageType.Index, ownerId: abandoned);
                page.MarkDirty();
                bracket.Commit();
            }
            database.Coordinator.Checkpoint(guard.Token);
            strategy.CopyDatabase("source", "image");
        }
        else if (kind == "drop")
        {
            await writer.ExecuteAsync("CREATE INDEX lost ON items(v)", cancellationToken: guard.Token);
            abandoned = database.DataStorage.GetIndexOwners(kept.OwnerId).Single().OwnerId;
            var dropPin = await database.Coordinator.BeginSnapshotPinAsync(guard.Token);
            await writer.ExecuteAsync("DROP INDEX lost ON items", cancellationToken: guard.Token);
            database.DataStorage.GetOwnerIndexPageCount(abandoned).ShouldBeGreaterThan(0);
            strategy.CopyDatabase("source", "image");
            await database.Coordinator.RollbackAsync(dropPin, guard.Token);
        }
        else
        {
            database.Catalog.AfterIndexRetirementMutation = stage =>
            {
                if (stage != "build-root") { return; }
                abandoned = database.DataStorage.GetIndexOwners(kept.OwnerId).Single().OwnerId;
                strategy.CopyDatabase("source", "image");
                throw new InvalidOperationException("injected root boundary");
            };
            try { await Should.ThrowAsync<InvalidOperationException>(async () => await writer.ExecuteAsync("CREATE INDEX lost ON items(v)", cancellationToken: guard.Token)); }
            finally { database.Catalog.AfterIndexRetirementMutation = null; }
        }
        int captures = 0;
        FaultInjectingJournalStorageStrategy.BeforeCommitFlush = () => strategy.CopyDatabase("image", "open" + captures++);

        // Act: each image interrupts an actual open-time detach or receipt-insert bracket.
        DocumentDatabase opened;
        try { opened = await engine.OpenDatabaseAsync("image", guard.Token); }
        finally { FaultInjectingJournalStorageStrategy.BeforeCommitFlush = null; }
        await opened.DisposeAsync();
        if (kind != "drop") { captures.ShouldBeGreaterThan(0); }

        // Assert: exact pages, registrations and surviving seeks after two reopens.
        for (int image = -1; image < captures; image++)
        {
            string name = image < 0 ? "image" : "open" + image;
            for (int reopen = 0; reopen < 2; reopen++)
            {
                var recovered = await engine.OpenDatabaseAsync(name, guard.Token);
                for (int pass = 0; pass < 8; pass++) { recovered.Coordinator.RunVersionPurgePass(guard.Token); }
                recovered.DataStorage.GetOwnerIndexPages(abandoned).ShouldBeEmpty();
                recovered.DataStorage.GetOwnerIndexPages(kept.OwnerId).ShouldBe(keptPages);
                recovered.DataStorage.GetIndexOwners(0).Select(owner => owner.OwnerId).ShouldBe([kept.OwnerId]);
                var pin = await recovered.Coordinator.BeginSnapshotPinAsync(guard.Token);
                try
                {
                    recovered.Catalog.GetIndexes(collectionId, pin.Snapshot).Select(index => index.Name).ShouldBe(["kept"]);
                    (await recovered.Catalog.SearchIndexAsync(collectionId, "kept", 1m, true, 1m, true, pin.Snapshot, guard.Token)).ShouldHaveSingleItem();
                }
                finally { await recovered.Coordinator.RollbackAsync(pin, guard.Token); }
                using (var registrations = recovered.DataStorage.GetUnitIterator(1))
                { int count = 0; while (registrations.MoveNext()) { count++; } count.ShouldBe(1); }
                await recovered.DisposeAsync();
            }
        }
    }
}
