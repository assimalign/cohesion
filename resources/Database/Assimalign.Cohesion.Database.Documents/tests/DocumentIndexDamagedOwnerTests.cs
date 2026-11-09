using System;
using System.Runtime.CompilerServices;
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
        // Arrange: allocation survives a failed root bracket, as in a canceled create.
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        await using var engine = DocumentDatabaseEngine.Create("failed-root", new()
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
        ScheduleRetirement(database.Catalog, owner);

        // Act.
        for (int pass = 0; pass < 8; pass++) { database.Coordinator.RunVersionPurgePass(guard.Token); }

        // Assert.
        database.DataStorage.GetOwnerIndexPages(owner).ShouldBeEmpty();
        database.IsOffline.ShouldBeFalse();
    }

    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "ScheduleIndexRetirement")]
    private static extern void ScheduleRetirement(Catalog.DocumentCatalog target, ulong owner);
}