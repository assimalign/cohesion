using System;
using System.Runtime.CompilerServices;
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
        // Arrange: allocation survives a failed root bracket, as in a canceled create.
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        await using var engine = GraphDatabaseEngine.Create("failed-root", new()
        { MaintenanceInterval = TimeSpan.FromHours(1), CheckpointInterval = TimeSpan.FromHours(1) });
        var database = await engine.CreateDatabaseAsync("test", guard.Token);
        ulong owner;
        do { owner = (ulong)database.DataStorage.ReserveTransactionSequence(); } while (owner <= 2);
        using (var bracket = database.DataStorage.BeginTransaction())
        {
            using var page = database.DataStorage.AllocatePageForWrite(bracket, PageType.Index, ownerId: owner);
            page.MarkDirty();
        }
        database.DataStorage.GetOwnerIndexPages(owner).ShouldHaveSingleItem();
        var horizon = await database.Coordinator.BeginSnapshotPinAsync(guard.Token);
        await database.Coordinator.RollbackAsync(horizon, guard.Token);
        ScheduleRetirement(database.Store, owner);

        // Act.
        for (int pass = 0; pass < 8; pass++) { database.Coordinator.RunVersionPurgePass(guard.Token); }

        // Assert.
        database.DataStorage.GetOwnerIndexPages(owner).ShouldBeEmpty();
        database.IsOffline.ShouldBeFalse();
    }

    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "ScheduleIndexReclamation")]
    private static extern void ScheduleRetirement(Storage.GraphStore target, ulong owner);
}