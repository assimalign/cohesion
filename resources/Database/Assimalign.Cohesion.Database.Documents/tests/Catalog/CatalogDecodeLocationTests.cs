using System;
using System.Buffers.Binary;
using System.IO;
using System.Threading.Tasks;
using Assimalign.Cohesion.Database.Documents.Internal;
using Assimalign.Cohesion.Database.Documents.Storage;
using Assimalign.Cohesion.Database.Storage;
using Assimalign.Cohesion.Database.Transactions;
using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Database.Documents.Catalog.Tests;

public sealed class CatalogDecodeLocationTests
{
    [Fact(DisplayName = "Cohesion Test [Documents.Catalog] - Exceptions: legacy constructors retain caller messages and causes (#1470)")]
    public void Exception_LegacyConstructors_ShouldRetainCallerContract()
    {
        var cause = new InvalidOperationException("caller cause");
        var plain = new DocumentCatalogException("caller message");
        var wrapped = new DocumentCatalogException("caller message", cause);
        plain.Message.ShouldBe("caller message");
        plain.InnerException.ShouldBeNull();
        plain.Diagnostics.ShouldBeEmpty();
        wrapped.Message.ShouldBe("caller message");
        wrapped.InnerException.ShouldBeSameAs(cause);
        wrapped.Diagnostics.ShouldBeEmpty();
    }

    [Theory(DisplayName = "Cohesion Test [Documents.Catalog] - Registration decoding: malformed index metadata reports page and slot (#1372)")]
    [InlineData("truncated", false)]
    [InlineData("header", false)]
    [InlineData("format", false)]
    [InlineData("identity", false)]
    [InlineData("name", false)]
    [InlineData("truncated", true)]
    [InlineData("header", true)]
    [InlineData("format", true)]
    [InlineData("identity", true)]
    [InlineData("name", true)]
    public async Task Read_MalformedRegistration_ShouldReportPhysicalLocation(string damage, bool fence)
    {
        await using var engine = DocumentDatabaseEngine.Create("decode-registration", new());
        var database = await engine.CreateDatabaseAsync("indexed");
        await using var session = await database.CreateSessionAsync();
        await session.CreateCollectionAsync("items");
        await session.ExecuteAsync("CREATE INDEX rank_index ON items (rank)");
        StorageUnit unit;
        using (var scan = database.DataStorage.GetUnitIterator(1))
        {
            scan.MoveNext().ShouldBeTrue();
            unit = scan.Current;
        }
        byte[] original = unit.Data.ToArray();
        byte[] malformed = damage == "truncated" ? original[..^1] : original.AsSpan().ToArray();
        if (damage == "header") { malformed[16] = byte.MaxValue; }
        if (damage == "format") { malformed[17] = byte.MaxValue; }
        if (damage == "identity") { BinaryPrimitives.WriteInt64LittleEndian(malformed.AsSpan(26, 8), 0); }
        if (damage == "name") { BinaryPrimitives.WriteInt32LittleEndian(malformed.AsSpan(34, 4), -2); }
        using (var bracket = database.DataStorage.BeginTransaction())
        {
            database.DataStorage.UpdateEntry(bracket, unit.PageId, unit.SlotIndex, malformed);
            bracket.Commit();
        }
        try
        {
            var failure = Should.Throw<DocumentCatalogException>(() =>
            {
                if (fence) { DocumentCatalog.EnsureIndexFormat(database.DataStorage); }
                else { DocumentCatalog.Open(database.DataStorage, database.Coordinator); }
            });
            failure.Diagnostics.Count.ShouldBe(1);
            var diagnostic = failure.Diagnostics[0];
            diagnostic.Code.ShouldBe("CD06105");
            diagnostic.Message.ShouldBe(failure.Message);
            diagnostic.Severity.ShouldBe(Assimalign.Cohesion.Database.Types.DiagnosticSeverity.Error);
            diagnostic.Start.ShouldBeNull();
            diagnostic.End.ShouldBeNull();
            failure.Message.ShouldContain($"page {(long)unit.PageId}", Case.Sensitive);
            failure.Message.ShouldContain($"slot {unit.SlotIndex}", Case.Sensitive);
            failure.PageId.ShouldBe(unit.PageId);
            failure.SlotIndex.ShouldBe(unit.SlotIndex);
            if (damage == "format")
            {
                failure.IsFormatRefusal.ShouldBeTrue();
                StorageCorruptionException.Find(failure).ShouldBeNull();
                failure.InnerException.ShouldBeOfType<DocumentCatalogException>().IsFormatRefusal.ShouldBeTrue();
            }
            else
            {
                var corruption = StorageCorruptionException.Find(failure).ShouldNotBeNull();
                corruption.PageId.ShouldBe(unit.PageId);
                corruption.SlotIndex.ShouldBe(unit.SlotIndex);
            }
        }
        finally
        {
            using var repair = database.DataStorage.BeginTransaction();
            database.DataStorage.UpdateEntry(repair, unit.PageId, unit.SlotIndex, original);
            repair.Commit();
        }
        Should.NotThrow(() => DocumentCatalog.Open(database.DataStorage, database.Coordinator));
    }

    [Fact(DisplayName = "Cohesion Test [Documents.Catalog] - Index decoding: a malformed matching document reports its metadata location (#1372)")]
    public async Task SearchIndex_MalformedDocumentMetadata_ShouldReportLocation()
    {
        await using var engine = DocumentDatabaseEngine.Create("decode", new());
        var database = await engine.CreateDatabaseAsync("indexed");
        await using var session = await database.CreateSessionAsync();
        await session.CreateCollectionAsync("items");
        var collection = await session.GetCollectionAsync("items");
        await collection.PutAsync(session, "one", "{\"rank\":7}"u8.ToArray());
        await session.ExecuteAsync("CREATE INDEX rank_index ON items (rank)");
        var reader = await database.Coordinator.BeginAsync(IsolationLevel.Snapshot);
        var metadata = database.Catalog.FindCollection("items", reader.Snapshot).ShouldNotBeNull();
        StorageUnit unit = default;
        using (var scan = database.DataStorage.GetUnitIterator(0))
        {
            while (scan.MoveNext())
            {
                if (scan.Current.Data.Span[16] == 2) { unit = scan.Current; break; }
            }
        }
        unit.Data.IsEmpty.ShouldBeFalse();
        byte[] original = unit.Data.ToArray();
        byte[] malformed = original.AsSpan().ToArray();
        malformed[16] = byte.MaxValue;
        using (var bracket = database.DataStorage.BeginTransaction())
        {
            database.DataStorage.UpdateEntry(bracket, unit.PageId, unit.SlotIndex, malformed);
            bracket.Commit();
        }
        try
        {
            var failure = await Should.ThrowAsync<DocumentCatalogException>(async () =>
                await database.Catalog.SearchIndexAsync(metadata.Id, "rank_index", 7, true, 7, true, reader.Snapshot));
            var corruption = StorageCorruptionException.Find(failure).ShouldNotBeNull();
            corruption.PageId.ShouldBe(unit.PageId);
            corruption.SlotIndex.ShouldBe(unit.SlotIndex);
        }
        finally
        {
            using var repair = database.DataStorage.BeginTransaction();
            database.DataStorage.UpdateEntry(repair, unit.PageId, unit.SlotIndex, original);
            repair.Commit();
        }
        (await database.Catalog.SearchIndexAsync(metadata.Id, "rank_index", 7, true, 7, true, reader.Snapshot))
            .ShouldHaveSingleItem().Id.ShouldBe("one");
        await database.Coordinator.RollbackAsync(reader);
    }

    [Theory(DisplayName = "Cohesion Test [Documents.Catalog] - Decoding: confirmed malformed metadata reports page and slot (#1372)")]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task Read_MalformedMetadata_ShouldRetainCatalogErrorAndReportLocation(bool cached, bool fence)
    {
        using var storage = DocumentStorage.Create(new MemoryStream(), new MemoryStream(), new MemoryStream(), "decode");
        await using var coordinator = new TransactionCoordinator(storage, storage.WriteAheadJournal, storage.Records);
        var id = Guid.NewGuid();
        byte[] original = DocumentCatalogCodec.Encode(new CatalogRecord(
            new DocumentCollectionMetadata(id, "items", DatabaseObjectOwner.Adhoc, null), null), TransactionSequence.None);
        (PageId PageId, int SlotIndex) location;
        using (var bracket = storage.BeginTransaction())
        {
            location = storage.InsertEntry(bracket, original);
            bracket.Commit();
        }
        var catalog = cached ? DocumentCatalog.Open(storage, coordinator) : null;
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
            var failure = Should.Throw<DocumentCatalogException>(() =>
            {
                if (fence) { DocumentCatalog.EnsureIndexFormat(storage); }
                else if (cached) { catalog!.FindCollection("items", reader.Snapshot); }
                else { DocumentCatalog.Open(storage, coordinator); }
            });
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
        (catalog ?? DocumentCatalog.Open(storage, coordinator)).FindCollection("items", reader.Snapshot)
            .ShouldNotBeNull().Id.ShouldBe(id);
        await coordinator.RollbackAsync(reader);
    }
}
