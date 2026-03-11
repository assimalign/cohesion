using System;
using System.ComponentModel;
using System.IO;
using System.Text;
using Xunit;

namespace Assimalign.Cohesion.Database.Sql.Storage.Tests;

using Assimalign.Cohesion.Database.Storage;

public class SqlStorageTests
{
    /// <summary>
    /// Helper to create a new SqlStorage backed by three in-memory streams.
    /// </summary>
    private static SqlStorage CreateInMemory(string name)
    {
        return SqlStorage.Create(new MemoryStream(), new MemoryStream(), new MemoryStream(), name);
    }

    [Fact]
    [DisplayName("Cohesion Test [SqlStorage] - Create: Should initialize new storage")]
    public void Create_ShouldInitializeNewStorage()
    {
        using var storage = CreateInMemory("users");

        Assert.NotEqual(default, storage.Id);
        Assert.Equal(StorageModel.Sql, storage.Model);
    }

    [Fact]
    [DisplayName("Cohesion Test [SqlStorage] - Create: Should accept StorageStream")]
    public void Create_ShouldAcceptStorageStream()
    {
        using var data = StorageStream.FromInMemory();
        using var journal = StorageStream.FromInMemory();
        using var backup = StorageStream.FromInMemory();
        using var storage = SqlStorage.Create(data, journal, backup, "users");

        Assert.NotEqual(default, storage.Id);
    }

    [Fact]
    [DisplayName("Cohesion Test [SqlStorage] - InsertRow: Should return valid page and slot")]
    public void InsertRow_ShouldReturnValidPageAndSlot()
    {
        using var storage = CreateInMemory("users");

        var row = new byte[68];
        BitConverter.TryWriteBytes(row, 1);
        Encoding.UTF8.GetBytes("Alice").CopyTo(row.AsSpan(4));

        var (pageId, slotIndex) = storage.InsertRow(row);

        Assert.True((long)pageId >= 1);
        Assert.Equal(0, slotIndex);
    }

    [Fact]
    [DisplayName("Cohesion Test [SqlStorage] - ReadRow: Should return inserted data")]
    public void ReadRow_ShouldReturnInsertedData()
    {
        using var storage = CreateInMemory("users");

        var original = new byte[68];
        BitConverter.TryWriteBytes(original, 42);
        Encoding.UTF8.GetBytes("Alice").CopyTo(original.AsSpan(4));

        var (pageId, slotIndex) = storage.InsertRow(original);
        var result = storage.ReadRow(pageId, slotIndex);

        Assert.Equal(original, result.ToArray());
    }

    [Fact]
    [DisplayName("Cohesion Test [SqlStorage] - InsertRow: Multiple inserts should produce sequential slots")]
    public void InsertRow_MultipleInserts_ShouldProduceSequentialSlots()
    {
        using var storage = CreateInMemory("users");

        byte[] MakeRow(int id)
        {
            var row = new byte[32];
            BitConverter.TryWriteBytes(row, id);
            return row;
        }

        var (page1, slot1) = storage.InsertRow(MakeRow(1));
        var (page2, slot2) = storage.InsertRow(MakeRow(2));
        var (page3, slot3) = storage.InsertRow(MakeRow(3));

        Assert.Equal(0, slot1);
        Assert.Equal(1, slot2);
        Assert.Equal(2, slot3);
        Assert.Equal(page1, page2);
        Assert.Equal(page2, page3);
    }

    [Fact]
    [DisplayName("Cohesion Test [SqlStorage] - ReadRow: Should preserve column values through roundtrip")]
    public void ReadRow_ShouldPreserveColumnValues()
    {
        using var storage = CreateInMemory("products");

        // Row layout: int32 Id (4 bytes) + double Price (8 bytes)
        var row = new byte[12];
        BitConverter.TryWriteBytes(row.AsSpan(0), 99);
        BitConverter.TryWriteBytes(row.AsSpan(4), 19.95);

        var (pageId, slotIndex) = storage.InsertRow(row);
        var result = storage.ReadRow(pageId, slotIndex);

        Assert.Equal(99, BitConverter.ToInt32(result.Span));
        Assert.Equal(19.95, BitConverter.ToDouble(result.Span.Slice(4)));
    }

    [Fact]
    [DisplayName("Cohesion Test [SqlStorage] - UpdateRow: Should overwrite existing data")]
    public void UpdateRow_ShouldOverwriteExistingData()
    {
        using var storage = CreateInMemory("users");

        var original = new byte[32];
        BitConverter.TryWriteBytes(original, 1);
        var (pageId, slotIndex) = storage.InsertRow(original);

        var updated = new byte[32];
        BitConverter.TryWriteBytes(updated, 999);
        storage.UpdateRow(pageId, slotIndex, updated);

        var result = storage.ReadRow(pageId, slotIndex);
        Assert.Equal(999, BitConverter.ToInt32(result.Span));
    }

    [Fact]
    [DisplayName("Cohesion Test [SqlStorage] - DeleteRow: Should prevent reading deleted record")]
    public void DeleteRow_ShouldPreventReadingDeletedRecord()
    {
        using var storage = CreateInMemory("users");

        var row = new byte[16];
        BitConverter.TryWriteBytes(row, 1);

        var (pageId, slotIndex) = storage.InsertRow(row);
        storage.DeleteRow(pageId, slotIndex);

        Assert.ThrowsAny<Exception>(() => storage.ReadRow(pageId, slotIndex));
    }

    [Fact]
    [DisplayName("Cohesion Test [SqlStorage] - FlushChanges: Should not throw")]
    public void FlushChanges_ShouldNotThrow()
    {
        using var storage = CreateInMemory("users");

        storage.InsertRow(new byte[16]);
        storage.FlushChanges();
    }

    [Fact]
    [DisplayName("Cohesion Test [SqlStorage] - Open: Should reopen and read persisted data")]
    public void Open_ShouldReopenAndReadPersistedData()
    {
        // Use a shared MemoryStream for data so we can reopen it.
        // The journal/backup get fresh streams each time because their content
        // is not needed across close/reopen for basic record reading.
        var dataBytes = new MemoryStream();
        PageId savedPageId;
        int savedSlot;

        var original = new byte[32];
        BitConverter.TryWriteBytes(original, 42);
        Encoding.UTF8.GetBytes("Persisted").CopyTo(original.AsSpan(4));

        using (var storage = SqlStorage.Create(
            new StorageStream(dataBytes),
            StorageStream.FromInMemory(),
            StorageStream.FromInMemory(),
            "persist-test"))
        {
            (savedPageId, savedSlot) = storage.InsertRow(original);
        }

        // dataBytes is disposed by StorageStream, so capture the buffer before dispose
        // Instead, use a non-disposing pattern: wrap with leaveOpen-like behavior
        // Actually, StorageStream disposes the inner stream. Let's use the buffer approach.
        // We need a different approach: create both streams fresh.

        // Better approach: write to a byte array, then reopen
        var dataBuffer = dataBytes.ToArray();

        using (var storage = SqlStorage.Open(
            new StorageStream(new MemoryStream(dataBuffer)),
            StorageStream.FromInMemory(),
            StorageStream.FromInMemory()))
        {
            var result = storage.ReadRow(savedPageId, savedSlot);
            Assert.Equal(original, result.ToArray());
        }
    }

    [Fact]
    [DisplayName("Cohesion Test [SqlStorage] - Open: Should reopen from raw Stream overload")]
    public void Open_ShouldReopenFromRawStream()
    {
        var dataStream = new MemoryStream();
        PageId savedPageId;
        int savedSlot;

        var original = new byte[20];
        BitConverter.TryWriteBytes(original, 7);

        using (var storage = SqlStorage.Create(
            new StorageStream(dataStream),
            StorageStream.FromInMemory(),
            StorageStream.FromInMemory(),
            "raw-test"))
        {
            (savedPageId, savedSlot) = storage.InsertRow(original);
        }

        var dataBuffer = dataStream.ToArray();

        using (var storage = SqlStorage.Open(
            new MemoryStream(dataBuffer),
            new MemoryStream(),
            new MemoryStream()))
        {
            var result = storage.ReadRow(savedPageId, savedSlot);
            Assert.Equal(original, result.ToArray());
        }
    }

    [Fact]
    [DisplayName("Cohesion Test [SqlStorage] - GetUnitIterator: Should iterate all living rows")]
    public void GetUnitIterator_ShouldIterateAllLivingRows()
    {
        using var storage = CreateInMemory("iter-test");

        byte[] MakeRow(int id)
        {
            var row = new byte[8];
            BitConverter.TryWriteBytes(row, id);
            return row;
        }

        storage.InsertRow(MakeRow(1));
        var (page2, slot2) = storage.InsertRow(MakeRow(2));
        storage.InsertRow(MakeRow(3));

        // Delete the second row
        storage.DeleteRow(page2, slot2);

        var ids = new System.Collections.Generic.List<int>();
        using var iterator = storage.GetUnitIterator();
        while (iterator.MoveNext())
        {
            ids.Add(BitConverter.ToInt32(iterator.Current.Data.Span));
        }

        Assert.Equal(2, ids.Count);
        Assert.Equal(1, ids[0]);
        Assert.Equal(3, ids[1]);
    }

    [Fact]
    [DisplayName("Cohesion Test [SqlStorage] - InsertRow: Large rows should span multiple pages")]
    public void InsertRow_LargeRows_ShouldSpanMultiplePages()
    {
        using var storage = CreateInMemory("large-test");

        var bigRow1 = new byte[4000];
        var bigRow2 = new byte[4000];
        var bigRow3 = new byte[4000];

        Random.Shared.NextBytes(bigRow1);
        Random.Shared.NextBytes(bigRow2);
        Random.Shared.NextBytes(bigRow3);

        var (page1, _) = storage.InsertRow(bigRow1);
        var (page2, _) = storage.InsertRow(bigRow2);
        var (page3, _) = storage.InsertRow(bigRow3);

        Assert.True((long)page3 > (long)page1, "Large rows should cause page overflow.");

        var result1 = storage.ReadRow(page1, 0);
        Assert.Equal(bigRow1, result1.ToArray());
    }

    [Fact]
    [DisplayName("Cohesion Test [SqlStorage] - Dispose: Should be idempotent")]
    public void Dispose_ShouldBeIdempotent()
    {
        var storage = SqlStorage.Create(new MemoryStream(), new MemoryStream(), new MemoryStream(), "dispose-test");
        storage.InsertRow(new byte[8]);
        storage.Dispose();
        storage.Dispose(); // Should not throw
    }
}
