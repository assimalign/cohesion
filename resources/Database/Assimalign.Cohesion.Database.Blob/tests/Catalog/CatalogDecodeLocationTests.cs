using System;
using System.IO;
using System.Threading.Tasks;
using Assimalign.Cohesion.Database.Blob.Internal;
using Assimalign.Cohesion.Database.Blob.Storage;
using Assimalign.Cohesion.Database.Storage;
using Assimalign.Cohesion.Database.Transactions;
using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Database.Blob.Catalog.Tests;

public sealed class CatalogDecodeLocationTests
{
    [Theory(DisplayName = "Cohesion Test [Blob.Catalog] - Decoding: confirmed malformed metadata reports page and slot (#1372)")]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task Read_MalformedMetadata_ShouldRetainCatalogErrorAndReportLocation(bool cached, bool format)
    {
        using var storage = BlobStorage.Create(new MemoryStream(), new MemoryStream(), new MemoryStream(), "decode");
        await using var coordinator = new TransactionCoordinator(storage, storage.WriteAheadJournal, storage.Records);
        var id = Guid.NewGuid();
        byte[] original = BlobCatalogCodec.Encode(new CatalogRecord(
            new BlobContainerMetadata(id, "items", DatabaseObjectOwner.Adhoc, null), null), TransactionSequence.None);
        (PageId PageId, int SlotIndex) location;
        using (var bracket = storage.BeginTransaction())
        {
            location = storage.InsertEntry(bracket, original);
            bracket.Commit();
        }
        var catalog = cached ? BlobCatalog.Open(storage, coordinator) : null;
        var reader = await coordinator.BeginAsync(IsolationLevel.Snapshot);
        byte[] malformed = original.AsSpan().ToArray();
        malformed[format ? 17 : 16] = byte.MaxValue;
        using (var bracket = storage.BeginTransaction())
        {
            storage.UpdateEntry(bracket, location.PageId, location.SlotIndex, malformed);
            bracket.Commit();
        }
        try
        {
            var failure = Should.Throw<BlobCatalogException>(() =>
            {
                if (cached) { catalog!.FindContainer("items", reader.Snapshot); }
                else { BlobCatalog.Open(storage, coordinator); }
            });
            failure.Message.ShouldContain($"page {(long)location.PageId}", Case.Sensitive);
            failure.Message.ShouldContain($"slot {location.SlotIndex}", Case.Sensitive);
            if (format)
            {
                failure.IsFormatRefusal.ShouldBeTrue();
                StorageCorruptionException.Find(failure).ShouldBeNull();
                failure.InnerException.ShouldBeOfType<BlobCatalogException>().IsFormatRefusal.ShouldBeTrue();
            }
            else
            {
                var corruption = StorageCorruptionException.Find(failure).ShouldNotBeNull();
                corruption.PageId.ShouldBe(location.PageId);
                corruption.SlotIndex.ShouldBe(location.SlotIndex);
                corruption.InnerException.ShouldNotBeNull();
            }
        }
        finally
        {
            using var repair = storage.BeginTransaction();
            storage.UpdateEntry(repair, location.PageId, location.SlotIndex, original);
            repair.Commit();
        }
        (catalog ?? BlobCatalog.Open(storage, coordinator)).FindContainer("items", reader.Snapshot)
            .ShouldNotBeNull().Id.ShouldBe(id);
        await coordinator.RollbackAsync(reader);
    }
}
