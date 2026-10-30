using System;
using System.IO;
using System.Threading.Tasks;
using Assimalign.Cohesion.Database.Graph.Internal;
using Assimalign.Cohesion.Database.Graph.Storage;
using Assimalign.Cohesion.Database.Storage;
using Assimalign.Cohesion.Database.Transactions;
using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Database.Graph.Catalog.Tests;

public sealed class CatalogDecodeLocationTests
{
    [Fact(DisplayName = "Cohesion Test [Graph.Catalog] - Exceptions: legacy constructors retain caller messages and causes (#1470)")]
    public void Exception_LegacyConstructors_ShouldRetainCallerContract()
    {
        var cause = new InvalidOperationException("caller cause");
        var plain = new GraphCatalogException("caller message");
        var wrapped = new GraphCatalogException("caller message", cause);
        plain.Message.ShouldBe("caller message");
        plain.InnerException.ShouldBeNull();
        plain.Diagnostics.ShouldBeEmpty();
        wrapped.Message.ShouldBe("caller message");
        wrapped.InnerException.ShouldBeSameAs(cause);
        wrapped.Diagnostics.ShouldBeEmpty();
    }

    [Theory(DisplayName = "Cohesion Test [Graph.Catalog] - Decoding: confirmed malformed metadata reports page and slot (#1372)")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Read_MalformedMetadata_ShouldRetainCatalogErrorAndReportLocation(bool cached)
    {
        using var storage = GraphStorage.Create(new MemoryStream(), new MemoryStream(), new MemoryStream(), "decode");
        await using var coordinator = new TransactionCoordinator(storage, storage.WriteAheadJournal, storage.Records);
        var id = Guid.NewGuid();
        byte[] original = GraphCatalogCodec.Encode(new CatalogRecord(
            label: new GraphLabelMetadata(id, "Person", DatabaseObjectOwner.Adhoc, null)), TransactionSequence.None);
        (PageId PageId, int SlotIndex) location;
        using (var bracket = storage.BeginTransaction())
        {
            location = storage.InsertEntry(bracket, original);
            bracket.Commit();
        }
        var catalog = cached ? GraphCatalog.Open(storage, coordinator) : null;
        var reader = await coordinator.BeginAsync(IsolationLevel.Snapshot);
        byte[] malformed = original.AsSpan().ToArray();
        malformed[16] = byte.MaxValue;
        using (var bracket = storage.BeginTransaction())
        {
            storage.UpdateEntry(bracket, location.PageId, location.SlotIndex, malformed);
            bracket.Commit();
        }
        try
        {
            var failure = Should.Throw<GraphCatalogException>(() =>
            {
                if (cached) { catalog!.FindLabel("Person", reader.Snapshot); }
                else { GraphCatalog.Open(storage, coordinator); }
            });
            failure.Diagnostics.Count.ShouldBe(1);
            var diagnostic = failure.Diagnostics[0];
            diagnostic.Code.ShouldBe("CG06006");
            diagnostic.Message.ShouldBe(failure.Message);
            diagnostic.Severity.ShouldBe(Assimalign.Cohesion.Database.Types.DiagnosticSeverity.Error);
            diagnostic.Start.ShouldBeNull();
            diagnostic.End.ShouldBeNull();
            failure.Message.ShouldContain($"page {(long)location.PageId}", Case.Sensitive);
            failure.Message.ShouldContain($"slot {location.SlotIndex}", Case.Sensitive);
            var corruption = StorageCorruptionException.Find(failure).ShouldNotBeNull();
            corruption.PageId.ShouldBe(location.PageId);
            corruption.SlotIndex.ShouldBe(location.SlotIndex);
            corruption.InnerException.ShouldNotBeNull();
        }
        finally
        {
            using var repair = storage.BeginTransaction();
            storage.UpdateEntry(repair, location.PageId, location.SlotIndex, original);
            repair.Commit();
        }
        (catalog ?? GraphCatalog.Open(storage, coordinator)).FindLabel("Person", reader.Snapshot)
            .ShouldNotBeNull().Id.ShouldBe(id);
        await coordinator.RollbackAsync(reader);
    }
}
