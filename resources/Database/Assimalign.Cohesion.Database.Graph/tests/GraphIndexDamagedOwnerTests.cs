using System;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;
using Xunit.Abstractions;

using Assimalign.Cohesion.Database.Graph.Tests.TestObjects;
using Assimalign.Cohesion.Database.Storage;
using Assimalign.Cohesion.Database.Transactions;

namespace Assimalign.Cohesion.Database.Graph.Tests;

[Collection(nameof(GraphConcurrencyCollection))]
public sealed class GraphIndexDamagedOwnerTests
{
    private readonly ITestOutputHelper _output;
    public GraphIndexDamagedOwnerTests(ITestOutputHelper output) { _output = output; }
    [Theory(DisplayName = "Cohesion Test [Graph] - Damaged unknown-owner tags refuse open before recording a receipt (#1808)")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OpenDatabaseAsync_DamagedOwnerTag_ShouldRefuseWithoutReceipt(bool engineOpen)
    {
        // Arrange.
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        var strategy = new FaultInjectingJournalStorageStrategy(durable: true);
        await using var engine = GraphDatabaseEngine.Create("damaged-owner", new()
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
            var direct = Should.Throw<StorageCorruptionException>(() => Storage.GraphStore.Open(storage, coordinator));
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
    [Fact(DisplayName = "Cohesion Test [Graph] - An allocated root without registration retires in the live process (#1808)")]
    public async Task ReclaimIndexes_RolledBackRootWithoutRegistration_ShouldRecordReceiptAndFree()
    {
        // Arrange: refuse registration inside the real root bracket after allocation.
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        await using var engine = GraphDatabaseEngine.Create("failed-root", new()
        { MaintenanceInterval = TimeSpan.FromHours(1), CheckpointInterval = TimeSpan.FromHours(1) });
        var database = await engine.CreateDatabaseAsync("test", guard.Token);

        ulong owner = 0;
        database.Store.IndexBuildStarted += id => owner = id;
        int refusals = 0;
        database.Store.BeforeIndexRegistrationInsert = () =>
        {
            database.DataStorage.GetOwnerIndexPages(owner).ShouldHaveSingleItem();
            var status = typeof(Storage.GraphStore).GetMethod("RetirementStatus", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                .Invoke(database.Store, [owner, database.Coordinator.Manager.OldestActive, false]);
            status.ShouldBe(1, "the creator must pin even an unregistered reserved owner");
            refusals++;
            throw new InvalidOperationException("injected registration refusal");
        };
        var context = await database.Coordinator.BeginAsync(IsolationLevel.Snapshot, guard.Token);
        try { await Should.ThrowAsync<InvalidOperationException>(async () => { await database.Store.CreateIndexAsync("N", "v", context, guard.Token); }); }
        finally
        {
            database.Store.BeforeIndexRegistrationInsert = null;
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
