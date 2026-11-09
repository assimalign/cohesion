using System;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;
using Xunit.Abstractions;

using Assimalign.Cohesion.Database.Storage;
using Assimalign.Cohesion.Database.Transactions;

namespace Assimalign.Cohesion.Database.Documents.Tests;

[Collection("Document index concurrency")]
public sealed class DocumentIndexDamagedOwnerTests
{
    private readonly ITestOutputHelper _output;
    public DocumentIndexDamagedOwnerTests(ITestOutputHelper output) { _output = output; }
    [Theory(DisplayName = "Cohesion Test [Documents] - Damaged unknown-owner tags refuse open before recording a receipt (#1808)")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OpenDatabaseAsync_DamagedOwnerTag_ShouldRefuseWithoutReceipt(bool engineOpen)
    {
        // Arrange.
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        var strategy = new FaultInjectingJournalStorageStrategy(durable: true);
        await using var engine = DocumentDatabaseEngine.Create("damaged-owner", new()
        { StorageStrategy = strategy, MaintenanceInterval = TimeSpan.FromHours(1), CheckpointInterval = TimeSpan.FromHours(1) });
        var database = await engine.CreateDatabaseAsync("source", guard.Token);
        PageId damaged;
        using (var admission = TransactionCheckpointInterlock.ForStorage(database.DataStorage).Enter("SeedOrphan", cancellationToken: guard.Token))
        using (var bracket = database.DataStorage.BeginTransaction())
        {
            using var page = database.DataStorage.AllocatePageForWrite(bracket, PageType.Index, ownerId: 1000000);
            damaged = page.Id;
            page.MarkDirty();
            bracket.Commit();
        }
        database.Coordinator.Checkpoint(guard.Token);
        await database.DisposeAsync();
        strategy.DamageOwnerTag("source", damaged);
        var before = strategy.Capture("source");

        // Act.
        if (!engineOpen)
        {
            using var storage = strategy.OpenStorage("source", null);
            await using var coordinator = new TransactionCoordinator(storage, storage.WriteAheadJournal, storage.Records);
            var direct = Should.Throw<StorageCorruptionException>(() => Catalog.DocumentCatalog.Open(storage, coordinator));
            direct.Code.ShouldBe("CK06003");
        }
        else
        {
            var error = await Should.ThrowAsync<DatabaseException>(async () => await engine.OpenDatabaseAsync("source", guard.Token));
            _output.WriteLine(error.ToString());
            error.Diagnostics.ShouldContain(item => item.Code == "CK06003");
        }

        // Assert: neither direct open nor engine recovery may record a bogus receipt.
        var after = strategy.Capture("source");
        after.Data.ShouldBe(before.Data);
        after.Journal.ShouldBe(before.Journal);
    }
    [Fact(DisplayName = "Cohesion Test [Documents] - An allocated root without registration retires in the live process (#1808)")]
    public async Task ReclaimIndexes_RolledBackRootWithoutRegistration_ShouldRecordReceiptAndFree()
    {
        // Arrange: refuse registration inside the real root bracket after allocation.
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        await using var engine = DocumentDatabaseEngine.Create("failed-root", new()
        { MaintenanceInterval = TimeSpan.FromHours(1), CheckpointInterval = TimeSpan.FromHours(1) });
        var database = await engine.CreateDatabaseAsync("test", guard.Token);
        var collection = new Catalog.DocumentCollectionMetadata(Guid.NewGuid(), "items");
        var seed = await database.Coordinator.BeginAsync(IsolationLevel.Snapshot, guard.Token);
        await database.Catalog.SaveCollectionAsync(collection, seed, guard.Token);
        await database.Coordinator.CommitAsync(seed, guard.Token);
        ulong owner = 0;
        database.Catalog.IndexBuildStarted += id => owner = id;
        int refusals = 0;
        database.Catalog.BeforeIndexRegistrationInsert = () =>
        {
            database.DataStorage.GetOwnerIndexPages(owner).ShouldHaveSingleItem();
            var status = typeof(Catalog.DocumentCatalog).GetMethod("IndexRetirementStatus", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                .Invoke(database.Catalog, [owner, database.Coordinator.Manager.OldestActive, false]);
            status.ShouldBe(1, "the creator must pin even an unregistered reserved owner");
            refusals++;
            throw new InvalidOperationException("injected registration refusal");
        };
        var context = await database.Coordinator.BeginAsync(IsolationLevel.Snapshot, guard.Token);
        try { await Should.ThrowAsync<InvalidOperationException>(async () => { await database.Catalog.CreateIndexAsync(collection.Id, "ix", "v", context, guard.Token); }); }
        finally
        {
            database.Catalog.BeforeIndexRegistrationInsert = null;
            await database.Coordinator.RollbackAsync(context, guard.Token);
        }
        refusals.ShouldBe(1);
        database.DataStorage.GetOwnerIndexPages(owner).ShouldHaveSingleItem();
        // Act.
        for (int pass = 0; pass < 8; pass++) { database.Coordinator.RunVersionPurgePass(guard.Token); }

        // Assert.
        database.DataStorage.GetOwnerIndexPages(owner).ShouldBeEmpty();
        database.IsOffline.ShouldBeFalse();
    }

}
