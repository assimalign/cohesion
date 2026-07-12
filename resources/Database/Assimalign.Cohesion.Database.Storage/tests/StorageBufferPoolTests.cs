using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Storage;
using Assimalign.Cohesion.Database.Storage.Units;

namespace Assimalign.Cohesion.Database.Storage.Tests;

/// <summary>
/// Tests for the buffer pool's allocation, pinning, LRU eviction, buffer reuse, and
/// concurrency behavior (#158/#159).
/// </summary>
public class StorageBufferPoolTests
{
    private static StorageStream NewStream() => new(new MemoryStream());

    private static StorageStream StreamWithPages(int pageCount)
    {
        var stream = new StorageStream(new MemoryStream());
        var pool = new StorageBufferPool(pageCount);

        for (long i = 0; i < pageCount; i++)
        {
            stream.SetLength((i + 1) * Page.Size);
            using var handle = pool.Pin((PageId)i, stream);
            var page = handle.Page; // struct copy shares the same pointer
            page.AsSpan().Clear();
            page.Id = i;
            page.Type = PageType.Data;
            handle.MarkDirty();
        }

        pool.FlushAll(stream);
        pool.Dispose();
        return stream;
    }

    [Fact(DisplayName = "Cohesion Test [Storage] - BufferPool: pin caches the page and repeated pins share the entry")]
    public void Pin_SamePageTwice_ShouldShareEntry()
    {
        // Arrange
        using var stream = StreamWithPages(1);
        using var pool = new StorageBufferPool(4);

        // Act
        var first = pool.Pin((PageId)0L, stream);
        var second = pool.Pin((PageId)0L, stream);

        // Assert
        pool.Count.ShouldBe(1);
        second.PinCount.ShouldBe(2);
        first.Dispose();
        second.PinCount.ShouldBe(1);
        second.Dispose();
    }

    [Fact(DisplayName = "Cohesion Test [Storage] - BufferPool: capacity overflow evicts the least recently used page")]
    public void Pin_AtCapacity_ShouldEvictLeastRecentlyUsed()
    {
        // Arrange: capacity 2, load pages 0 and 1, touch 0 so 1 becomes LRU.
        using var stream = StreamWithPages(3);
        using var pool = new StorageBufferPool(2);

        pool.Pin((PageId)0L, stream).Dispose();
        pool.Pin((PageId)1L, stream).Dispose();
        pool.Pin((PageId)0L, stream).Dispose(); // touch 0 -> 1 is now LRU

        // Act: loading page 2 must evict page 1 (LRU), not page 0.
        pool.Pin((PageId)2L, stream).Dispose();

        // Assert
        pool.TryGet((PageId)0L, out var handle0).ShouldBeTrue();
        handle0!.Dispose();
        pool.TryGet((PageId)1L, out _).ShouldBeFalse();
        pool.TryGet((PageId)2L, out var handle2).ShouldBeTrue();
        handle2!.Dispose();
    }

    [Fact(DisplayName = "Cohesion Test [Storage] - BufferPool: pinned pages are never evicted")]
    public void Pin_AtCapacityWithPinnedLru_ShouldSkipPinnedPage()
    {
        // Arrange: capacity 2; page 0 stays pinned (it is the LRU candidate).
        using var stream = StreamWithPages(3);
        using var pool = new StorageBufferPool(2);

        var pinned = pool.Pin((PageId)0L, stream);
        pool.Pin((PageId)1L, stream).Dispose();

        // Act: page 2 must evict page 1 even though page 0 is least recently used.
        pool.Pin((PageId)2L, stream).Dispose();

        // Assert
        pool.TryGet((PageId)0L, out var handle0).ShouldBeTrue();
        handle0!.Dispose();
        pool.TryGet((PageId)1L, out _).ShouldBeFalse();
        pinned.Dispose();
    }

    [Fact(DisplayName = "Cohesion Test [Storage] - BufferPool: all pages pinned at capacity fails loudly")]
    public void Pin_AllPagesPinnedAtCapacity_ShouldThrow()
    {
        // Arrange
        using var stream = StreamWithPages(3);
        using var pool = new StorageBufferPool(2);

        var handle0 = pool.Pin((PageId)0L, stream);
        var handle1 = pool.Pin((PageId)1L, stream);

        // Act / Assert
        Should.Throw<StorageIOException>(() => pool.Pin((PageId)2L, stream));

        handle0.Dispose();
        handle1.Dispose();
    }

    [Fact(DisplayName = "Cohesion Test [Storage] - BufferPool: dirty pages are written back with a checksum on eviction")]
    public void Evict_DirtyPage_ShouldPersistWithChecksum()
    {
        // Arrange
        using var stream = StreamWithPages(1);
        using var pool = new StorageBufferPool(2);

        using (var handle = pool.Pin((PageId)0L, stream))
        {
            handle.Page.AsBodySpan()[0] = 0xAB;
            handle.MarkDirty();
        }

        // Act
        pool.Evict((PageId)0L, stream);

        // Assert: reload verifies the stamped checksum and sees the mutation.
        using var reloaded = pool.Pin((PageId)0L, stream);
        reloaded.Page.AsBodySpan()[0].ShouldBe((byte)0xAB);
        reloaded.Page.Checksum.ShouldNotBe(0u);
    }

    [Fact(DisplayName = "Cohesion Test [Storage] - BufferPool: evicting a pinned page throws")]
    public void Evict_PinnedPage_ShouldThrow()
    {
        // Arrange
        using var stream = StreamWithPages(1);
        using var pool = new StorageBufferPool(2);
        var handle = pool.Pin((PageId)0L, stream);

        // Act / Assert
        Should.Throw<StorageIOException>(() => pool.Evict((PageId)0L, stream));
        handle.Dispose();
    }

    [Fact(DisplayName = "Cohesion Test [Storage] - BufferPool: evicted buffers are recycled without leaking stale content")]
    public void Pin_AfterEviction_ShouldNotLeakStaleBufferContent()
    {
        // Arrange: write a recognizable pattern to page 0, evict it, then load a
        // brand-new page (beyond stream length) which must arrive zeroed.
        using var stream = StreamWithPages(1);
        using var pool = new StorageBufferPool(1);

        using (var handle = pool.Pin((PageId)0L, stream))
        {
            handle.Page.AsBodySpan().Fill(0xEE);
            handle.MarkDirty();
        }

        // Act: page 5 is beyond the stream, so it is a fresh page reusing the buffer.
        using var fresh = pool.Pin((PageId)5L, stream);

        // Assert
        fresh.Page.AsBodySpan().ToArray().ShouldAllBe(value => value == 0);
    }

    [Fact(DisplayName = "Cohesion Test [Storage] - BufferPool: corrupted page load does not poison the cache")]
    public void Pin_CorruptedPage_ShouldThrowAndStayOutOfCache()
    {
        // Arrange: persist a valid page then flip a byte on disk.
        var memory = new MemoryStream();
        var stream = new StorageStream(memory);
        using (var setupPool = new StorageBufferPool(1))
        {
            stream.SetLength(Page.Size);
            using (var handle = setupPool.Pin((PageId)0L, stream))
            {
                var page = handle.Page; // struct copy shares the same pointer
                page.Type = PageType.Data;
                handle.MarkDirty();
            }
            setupPool.FlushAll(stream);
        }

        var bytes = memory.ToArray();
        bytes[Page.HeaderSize + 3] ^= 0xFF;
        using var corrupted = new StorageStream(new MemoryStream(bytes));
        using var pool = new StorageBufferPool(2);

        // Act / Assert
        Should.Throw<StorageCorruptionException>(() => pool.Pin((PageId)0L, corrupted));
        pool.Count.ShouldBe(0);
        stream.Dispose();
    }

    [Fact(DisplayName = "Cohesion Test [Storage] - BufferPool: concurrent pin/unpin churn stays consistent")]
    public async Task Pin_ConcurrentChurn_ShouldStayConsistent()
    {
        // Arrange: 16 pages, capacity 8, hammered by 8 workers.
        using var stream = StreamWithPages(16);
        using var pool = new StorageBufferPool(8);

        // Act
        var workers = Enumerable.Range(0, 8).Select(worker => Task.Run(() =>
        {
            var random = new Random(worker * 7919);
            for (int i = 0; i < 500; i++)
            {
                long pageId = random.Next(0, 16);
                using var handle = pool.Pin((PageId)pageId, stream);
                handle.Page.Id.ShouldBe(pageId);
            }
        }));

        await Task.WhenAll(workers);

        // Assert: capacity was respected and every entry is unpinned.
        pool.Count.ShouldBeLessThanOrEqualTo(8);
        for (long i = 0; i < 16; i++)
        {
            if (pool.TryGet((PageId)i, out var handle))
            {
                handle!.PinCount.ShouldBe(1);
                handle.Dispose();
            }
        }
    }

    [Fact(DisplayName = "Cohesion Test [Storage] - BufferPool: concurrent writers persist every mutation")]
    public async Task FlushAll_AfterConcurrentWrites_ShouldPersistEveryMutation()
    {
        // Arrange: each worker owns one page and increments its first body byte.
        using var stream = StreamWithPages(4);
        using var pool = new StorageBufferPool(4);

        // Act
        var workers = Enumerable.Range(0, 4).Select(worker => Task.Run(() =>
        {
            for (int i = 0; i < 100; i++)
            {
                using var handle = pool.Pin((PageId)(long)worker, stream);
                handle.Page.AsBodySpan()[0]++;
                handle.MarkDirty();
            }
        }));

        await Task.WhenAll(workers);
        pool.FlushAll(stream);

        // Assert
        using var verifyPool = new StorageBufferPool(4);
        for (long i = 0; i < 4; i++)
        {
            using var handle = verifyPool.Pin((PageId)i, stream);
            handle.Page.AsBodySpan()[0].ShouldBe((byte)100);
        }
    }

    [Fact(DisplayName = "Cohesion Test [Storage] - PageManager: freed pages are recycled by the next allocation")]
    public void AllocatePage_AfterFree_ShouldRecycleFreedPage()
    {
        // Arrange
        using var stream = NewStream();
        var pool = new StorageBufferPool(4);
        var map = new StorageFreeSpaceMap();
        using var manager = new StoragePageManager(stream, pool, map);

        var first = manager.AllocatePage(PageType.Data);
        long firstId = (long)first.Id;
        first.Dispose();

        // Act
        manager.FreePage((PageId)firstId);
        var second = manager.AllocatePage(PageType.Data);

        // Assert
        ((long)second.Id).ShouldBe(firstId);
        second.Page.Type.ShouldBe(PageType.Data);
        second.Dispose();
        pool.Dispose();
    }

    [Fact(DisplayName = "Cohesion Test [Storage] - PageManager: reading an unallocated page throws")]
    public void GetPage_UnallocatedPage_ShouldThrow()
    {
        // Arrange
        using var stream = NewStream();
        var pool = new StorageBufferPool(4);
        var map = new StorageFreeSpaceMap();
        using var manager = new StoragePageManager(stream, pool, map);

        // Act / Assert
        Should.Throw<StorageIOException>(() => manager.GetPage((PageId)7L));
        pool.Dispose();
    }
}
