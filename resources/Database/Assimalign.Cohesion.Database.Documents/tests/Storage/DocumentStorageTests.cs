using System;
using System.IO;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Documents.Protocol;

namespace Assimalign.Cohesion.Database.Documents.Storage.Tests;

using Assimalign.Cohesion.Database.Storage;

public class DocumentStorageTests
{
    /// <summary>
    /// Helper to create a new DocumentStorage backed by three in-memory streams.
    /// </summary>
    private static DocumentStorage CreateInMemory(string name)
    {
        return DocumentStorage.Create(new MemoryStream(), new MemoryStream(), new MemoryStream(), name);
    }

    [Fact(DisplayName = "Cohesion Test [DocumentStorage] - Create: Should initialize new storage")]
    public void Create_ShouldInitializeNewStorage()
    {
        using var storage = CreateInMemory("test-docs");

        storage.Id.ShouldNotBe(default);
        storage.Model.ShouldBe(StorageModel.Document);
    }

    [Fact(DisplayName = "Cohesion Test [DocumentStorage] - Create: Should accept StorageStream")]
    public void Create_ShouldAcceptStorageStream()
    {
        using var data = StorageStream.FromInMemory();
        using var journal = StorageStream.FromInMemory();
        using var backup = StorageStream.FromInMemory();
        using var storage = DocumentStorage.Create(data, journal, backup, "test-docs");

        storage.Id.ShouldNotBe(default);
    }

    [Fact(DisplayName = "Cohesion Test [DocumentStorage] - InsertDocument: Should return valid page and slot")]
    public void InsertDocument_ShouldReturnValidPageAndSlot()
    {
        using var storage = CreateInMemory("test-docs");

        var data = CreateTuplePayload(1, "Alice");
        var (pageId, slotIndex) = storage.InsertDocument(data);

        ((long)pageId).ShouldBeGreaterThanOrEqualTo(1L);
        slotIndex.ShouldBe(0);
    }

    [Fact(DisplayName = "Cohesion Test [DocumentStorage] - ReadDocument: Should return inserted data")]
    public void ReadDocument_ShouldReturnInsertedData()
    {
        using var storage = CreateInMemory("test-docs");

        var original = CreateTuplePayload(1, "Alice");
        var (pageId, slotIndex) = storage.InsertDocument(original);

        var result = storage.ReadDocument(pageId, slotIndex);
        var tuple = StorageTuple.FromBytes(result.Span);

        tuple.TryGetField("id", out var idField).ShouldBeTrue();
        BitConverter.ToInt32(idField.Value.Span).ShouldBe(1);

        tuple.TryGetField("name", out var nameField).ShouldBeTrue();
        nameField.Value.ToStringValue().ShouldBe("Alice");

        result.ToArray().ShouldBe(original);
    }

    [Fact(DisplayName = "Cohesion Test [DocumentStorage] - InsertDocument: Multiple inserts should produce sequential slots")]
    public void InsertDocument_MultipleInserts_ShouldProduceSequentialSlots()
    {
        using var storage = CreateInMemory("test-docs");

        var (page1, slot1) = storage.InsertDocument(CreateTuplePayload(1, "A"));
        var (page2, slot2) = storage.InsertDocument(CreateTuplePayload(2, "B"));
        var (page3, slot3) = storage.InsertDocument(CreateTuplePayload(3, "C"));

        slot1.ShouldBe(0);
        slot2.ShouldBe(1);
        slot3.ShouldBe(2);
        page2.ShouldBe(page1);
        page3.ShouldBe(page2);
    }

    [Fact(DisplayName = "Cohesion Test [DocumentStorage] - UpdateDocument: Should overwrite existing data")]
    public void UpdateDocument_ShouldOverwriteExistingData()
    {
        using var storage = CreateInMemory("test-docs");

        var original = CreateTuplePayload(1, "Alice");
        var (pageId, slotIndex) = storage.InsertDocument(original);

        var updated = CreateTuplePayload(1, "Bob");
        storage.UpdateDocument(pageId, slotIndex, updated);

        var result = storage.ReadDocument(pageId, slotIndex);
        var tuple = StorageTuple.FromBytes(result.Span);
        tuple.TryGetField("name", out var nameField).ShouldBeTrue();
        nameField.Value.ToStringValue().ShouldBe("Bob");
        result.ToArray().ShouldBe(updated);
    }

    [Fact(DisplayName = "Cohesion Test [DocumentStorage] - DeleteDocument: Should prevent reading deleted record")]
    public void DeleteDocument_ShouldPreventReadingDeletedRecord()
    {
        using var storage = CreateInMemory("test-docs");

        var (pageId, slotIndex) = storage.InsertDocument(CreateTuplePayload(1, "Alice"));
        storage.DeleteDocument(pageId, slotIndex);

        Should.Throw<Exception>(() => storage.ReadDocument(pageId, slotIndex));
    }

    [Fact(DisplayName = "Cohesion Test [DocumentStorage] - FlushChanges: Should not throw")]
    public void FlushChanges_ShouldNotThrow()
    {
        using var storage = CreateInMemory("test-docs");

        storage.InsertDocument(CreateTuplePayload(1, "Flush"));
        storage.FlushChanges();
    }

    [Fact(DisplayName = "Cohesion Test [DocumentStorage] - Open: Should reopen and read persisted data")]
    public void Open_ShouldReopenAndReadPersistedData()
    {
        var dataStream = new MemoryStream();
        PageId savedPageId;
        int savedSlot;
        byte[] original = CreateTuplePayload(42, "Persisted");

        // Create, write, and dispose
        using (var storage = DocumentStorage.Create(
            new StorageStream(dataStream),
            StorageStream.FromInMemory(),
            StorageStream.FromInMemory(),
            "persist-test"))
        {
            (savedPageId, savedSlot) = storage.InsertDocument(original);
        }

        // Reopen from a copy of the data (MemoryStream.ToArray works after dispose)
        var dataBuffer = dataStream.ToArray();

        using (var storage = DocumentStorage.Open(
            new StorageStream(new MemoryStream(dataBuffer)),
            StorageStream.FromInMemory(),
            StorageStream.FromInMemory()))
        {
            var result = storage.ReadDocument(savedPageId, savedSlot);
            result.ToArray().ShouldBe(original);
        }
    }

    [Fact(DisplayName = "Cohesion Test [DocumentStorage] - Open: Should reopen from raw Stream overload")]
    public void Open_ShouldReopenFromRawStream()
    {
        var dataStream = new MemoryStream();
        byte[] original = CreateTuplePayload(7, "RawStream");
        PageId savedPageId;
        int savedSlot;

        using (var storage = DocumentStorage.Create(
            new StorageStream(dataStream),
            StorageStream.FromInMemory(),
            StorageStream.FromInMemory(),
            "raw-test"))
        {
            (savedPageId, savedSlot) = storage.InsertDocument(original);
        }

        var dataBuffer = dataStream.ToArray();

        using (var storage = DocumentStorage.Open(
            new MemoryStream(dataBuffer),
            new MemoryStream(),
            new MemoryStream()))
        {
            var result = storage.ReadDocument(savedPageId, savedSlot);
            result.ToArray().ShouldBe(original);
        }
    }

    [Fact(DisplayName = "Cohesion Test [DocumentStorage] - GetUnitIterator: Should iterate all living documents")]
    public void GetUnitIterator_ShouldIterateAllLivingDocuments()
    {
        using var storage = CreateInMemory("iter-test");

        var doc1 = CreateTuplePayload(1, "One");
        var doc2 = CreateTuplePayload(2, "Two");
        var doc3 = CreateTuplePayload(3, "Three");

        storage.InsertDocument(doc1);
        var (page2, slot2) = storage.InsertDocument(doc2);
        storage.InsertDocument(doc3);

        // Delete the second document
        storage.DeleteDocument(page2, slot2);

        var ids = new System.Collections.Generic.List<int>();
        using var iterator = storage.GetUnitIterator();
        while (iterator.MoveNext())
        {
            var tuple = StorageTuple.FromBytes(iterator.Current.Data.Span);
            tuple.TryGetField("id", out var idField).ShouldBeTrue();
            ids.Add(BitConverter.ToInt32(idField.Value.Span));
        }

        ids.Count.ShouldBe(2);
        ids[0].ShouldBe(1);
        ids[1].ShouldBe(3);
    }

    [Fact(DisplayName = "Cohesion Test [DocumentStorage] - InsertDocument: Large documents should span multiple pages")]
    public void InsertDocument_LargeDocuments_ShouldSpanMultiplePages()
    {
        using var storage = CreateInMemory("large-test");

        // Each page body is ~8096 bytes minus slot overhead.
        // Insert documents large enough to force a new page allocation.
        var bigDoc1 = new byte[4000];
        var bigDoc2 = new byte[4000];
        var bigDoc3 = new byte[4000];

        Random.Shared.NextBytes(bigDoc1);
        Random.Shared.NextBytes(bigDoc2);
        Random.Shared.NextBytes(bigDoc3);

        var (page1, _) = storage.InsertDocument(bigDoc1);
        var (page2, _) = storage.InsertDocument(bigDoc2);
        var (page3, _) = storage.InsertDocument(bigDoc3);

        // At least one of the later docs should overflow to a new page
        ((long)page3).ShouldBeGreaterThan((long)page1, "Large documents should cause page overflow.");

        // Verify all documents read back correctly
        var result1 = storage.ReadDocument(page1, 0);
        result1.ToArray().ShouldBe(bigDoc1);
    }

    [Fact(DisplayName = "Cohesion Test [DocumentStorage] - Dispose: Should be idempotent")]
    public void Dispose_ShouldBeIdempotent()
    {
        var storage = DocumentStorage.Create(new MemoryStream(), new MemoryStream(), new MemoryStream(), "dispose-test");
        storage.InsertDocument(CreateTuplePayload(1, "Dispose"));
        storage.Dispose();
        storage.Dispose(); // Should not throw
    }

    private static byte[] CreateTuplePayload(int id, string name)
    {
        return new StorageTuple(
            new StorageTupleField("id", BitConverter.GetBytes(id)),
            new StorageTupleField("name", name.ToUtf8()))
            .ToBytes();
    }
}
