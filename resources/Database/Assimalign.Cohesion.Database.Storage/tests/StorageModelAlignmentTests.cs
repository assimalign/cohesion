using System;
using System.IO;
using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Storage;
using Assimalign.Cohesion.Database.Storage.Units;

namespace Assimalign.Cohesion.Database.Storage.Tests;

/// <summary>
/// Tests for the aligned storage model (#157): page header layout with LSN and
/// checksum, checksum verification on the read path, free-space map reconstruction
/// on reopen, and the storage exception family.
/// </summary>
public class StorageModelAlignmentTests
{
    [Fact(DisplayName = "Cohesion Test [Storage] - Page: header round-trips id, LSN, checksum, type, and flags")]
    public unsafe void PageHeader_SetFields_ShouldRoundTrip()
    {
        // Arrange
        var buffer = new byte[Page.Size];

        fixed (byte* pointer = buffer)
        {
            var page = new Page(pointer);

            // Act
            page.Id = 42;
            page.Lsn = 123456789L;
            page.Checksum = 0xDEADBEEF;
            page.Type = PageType.Index;
            page.Flags = PageFlags.Overflow;
            page.OverflowSize = 1024;

            // Assert
            page.Id.ShouldBe(42);
            page.Lsn.ShouldBe(123456789L);
            page.Checksum.ShouldBe(0xDEADBEEFu);
            page.Type.ShouldBe(PageType.Index);
            page.Flags.ShouldBe(PageFlags.Overflow);
            page.IsOverflow.ShouldBeTrue();
            page.OverflowSize.ShouldBe(1024);
        }
    }

    [Fact(DisplayName = "Cohesion Test [Storage] - Page: zero-initialized page reads as free")]
    public unsafe void PageHeader_ZeroInitialized_ShouldReadAsFree()
    {
        // Arrange
        var buffer = new byte[Page.Size];

        fixed (byte* pointer = buffer)
        {
            var page = new Page(pointer);

            // Assert
            page.Type.ShouldBe(PageType.Free);
            page.Flags.ShouldBe(PageFlags.None);
            page.Lsn.ShouldBe(0L);
            page.Checksum.ShouldBe(0u);
        }
    }

    [Fact(DisplayName = "Cohesion Test [Storage] - Checksum: corrupted page fails verification on load")]
    public void BufferPool_CorruptedPage_ShouldThrowStorageCorruptionException()
    {
        // Arrange: create storage with one record, flush it, and capture the bytes.
        var dataStream = new MemoryStream();
        var storage = TestStorage.Create(dataStream);
        storage.Insert(new byte[] { 1, 2, 3, 4 });
        storage.FlushAll();
        storage.Dispose();

        // Corrupt one byte in the body of page 1 (the data page).
        var bytes = dataStream.ToArray();
        bytes[Page.Size + Page.HeaderSize + 1] ^= 0xFF;

        // Act / Assert: reopening and reading the corrupted page must fail loudly.
        var reopened = TestStorage.Open(new MemoryStream(bytes));
        var exception = Should.Throw<StorageCorruptionException>(() => reopened.Read((PageId)1L, 0));
        exception.PageId.HasValue.ShouldBeTrue();
        ((long)exception.PageId!.Value).ShouldBe(1L);
        reopened.Dispose();
    }

    [Fact(DisplayName = "Cohesion Test [Storage] - Checksum: intact page verifies and reads back after reopen")]
    public void BufferPool_IntactPage_ShouldReadBackAfterReopen()
    {
        // Arrange
        var dataStream = new MemoryStream();
        var storage = TestStorage.Create(dataStream);
        var (pageId, slotIndex) = storage.Insert(new byte[] { 9, 8, 7 });
        storage.FlushAll();
        storage.Dispose();

        // Act
        var reopened = TestStorage.Open(new MemoryStream(dataStream.ToArray()));
        var data = reopened.Read(pageId, slotIndex);

        // Assert
        data.ToArray().ShouldBe(new byte[] { 9, 8, 7 });
        reopened.Dispose();
    }

    [Fact(DisplayName = "Cohesion Test [Storage] - FreeSpaceMap: freed pages are recycled before extending the file")]
    public void FreeSpaceMap_FreedPage_ShouldBeRecycled()
    {
        // Arrange
        var map = new StorageFreeSpaceMap();
        var first = map.Allocate();
        var second = map.Allocate();
        map.Allocate();

        // Act
        map.Free(second);
        var recycled = map.Allocate();

        // Assert
        recycled.ShouldBe(second);
        map.IsAllocated(first).ShouldBeTrue();
        map.IsAllocated(recycled).ShouldBeTrue();
    }

    [Fact(DisplayName = "Cohesion Test [Storage] - FreeSpaceMap: freeing an unallocated page throws")]
    public void FreeSpaceMap_FreeUnallocatedPage_ShouldThrow()
    {
        // Arrange
        var map = new StorageFreeSpaceMap();
        map.Allocate();

        // Act / Assert
        Should.Throw<StorageIOException>(() => map.Free((PageId)5L));
    }

    [Fact(DisplayName = "Cohesion Test [Storage] - Reopen: free pages are rediscovered from page headers")]
    public void OpenExisting_FreedPage_ShouldRebuildFreeList()
    {
        // Arrange: fill enough records to allocate several data pages, then free one.
        var dataStream = new MemoryStream();
        var storage = TestStorage.Create(dataStream);

        var payload = new byte[4000];
        storage.Insert(payload);        // page 1 (two records fit per page)
        storage.Insert(payload);        // page 1
        storage.Insert(payload);        // page 1 full -> page 2
        storage.FreeDataPage((PageId)2L);
        storage.FlushAll();
        storage.Dispose();

        // Act
        var reopened = TestStorage.Open(new MemoryStream(dataStream.ToArray()));

        // Assert: page 2 is free again and available for recycling.
        reopened.IsPageAllocated((PageId)2L).ShouldBeFalse();
        reopened.FreePageCount.ShouldBe(1L);
        reopened.Dispose();
    }

    [Fact(DisplayName = "Cohesion Test [Storage] - Reopen: records survive and free pages are skipped by iteration")]
    public void OpenExisting_AfterFree_IteratorSkipsFreedPages()
    {
        // Arrange
        var dataStream = new MemoryStream();
        var storage = TestStorage.Create(dataStream);

        var payload = new byte[4000];
        storage.Insert(payload);        // page 1
        storage.Insert(payload);        // page 1
        storage.Insert(payload);        // page 2
        storage.FreeDataPage((PageId)2L);
        storage.FlushAll();
        storage.Dispose();

        // Act
        var reopened = TestStorage.Open(new MemoryStream(dataStream.ToArray()));
        int count = 0;
        using (var iterator = reopened.GetUnitIterator())
        {
            while (iterator.MoveNext())
            {
                count++;
            }
        }

        // Assert: two of the three records remain (page 2's record freed with the page).
        count.ShouldBe(2);
        reopened.Dispose();
    }

    [Fact(DisplayName = "Cohesion Test [Storage] - Exceptions: journal and corruption exceptions share the storage root")]
    public void StorageExceptions_ShouldShareStorageExceptionRoot()
    {
        // Assert
        typeof(JournalException).IsSubclassOf(typeof(StorageException)).ShouldBeTrue();
        typeof(StorageCorruptionException).IsSubclassOf(typeof(StorageException)).ShouldBeTrue();
        typeof(StorageIOException).IsSubclassOf(typeof(StorageException)).ShouldBeTrue();
        typeof(SlottedPageException).IsSubclassOf(typeof(StorageException)).ShouldBeTrue();
    }

    [Fact(DisplayName = "Cohesion Test [Storage] - InsertRecord: record larger than a page is rejected")]
    public void InsertRecord_OversizedRecord_ShouldThrow()
    {
        // Arrange
        var storage = TestStorage.Create(new MemoryStream());

        // Act / Assert
        Should.Throw<SlottedPageException>(() => storage.Insert(new byte[SlottedPage.MaxRecordSize + 1]));
        storage.Dispose();
    }

    /// <summary>
    /// Minimal concrete storage over caller-owned streams. The data stream is wrapped
    /// with <c>leaveOpen</c> semantics by copying, so tests can capture flushed bytes.
    /// </summary>
    private sealed class TestStorage : Storage
    {
        private TestStorage(StorageStream data, StorageStream journal, StorageStream backup)
            : base(data, journal, backup, bufferPoolCapacity: 8)
        {
        }

        public override StorageModel Model => StorageModel.Custom;

        public static TestStorage Create(Stream data)
        {
            var storage = new TestStorage(
                new StorageStream(data),
                new StorageStream(new MemoryStream()),
                new StorageStream(new MemoryStream()));
            storage.InitializeNew((Name)"test");
            return storage;
        }

        public static TestStorage Open(Stream data)
        {
            var storage = new TestStorage(
                new StorageStream(data),
                new StorageStream(new MemoryStream()),
                new StorageStream(new MemoryStream()));
            storage.OpenExisting();
            return storage;
        }

        public (PageId PageId, int SlotIndex) Insert(byte[] data) => InsertRecord(data);

        public ReadOnlyMemory<byte> Read(PageId pageId, int slotIndex) => ReadRecord(pageId, slotIndex);

        public void FreeDataPage(PageId pageId) => PageManager.FreePage(pageId);

        public bool IsPageAllocated(PageId pageId) => FreeSpaceMap.IsAllocated(pageId);

        public long FreePageCount => FreeSpaceMap.FreePageCount;

        public void FlushAll() => Flush();
    }
}
