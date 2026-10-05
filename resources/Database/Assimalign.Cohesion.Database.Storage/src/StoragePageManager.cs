using System;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Database.Storage;

using Assimalign.Cohesion.Database.Storage.Internal;
using Assimalign.Cohesion.Database.Storage.Units;

/// <summary>
/// Coordinates page-level operations for a storage file — allocation, deallocation, retrieval
/// and flushing — by managing the buffer pool, free space map, and storage stream together.
/// Every database model uses it, through <see cref="Storage.PageManager"/>, for its page storage.
/// </summary>
/// <remarks>
/// Page 0 is the file header (storage format 2). The storage rewrites its header slots in place
/// with positional writes that bypass the buffer pool, so a pooled copy of page 0 would be stale
/// after the next header write, and its page checksum is zero, so a stale or damaged copy would
/// never be caught. A write-back of one, or a journaled image of it that recovery replays, would
/// roll both header slots back. The manager therefore reserves page 0 in the free-space map and
/// refuses to pin or free it, whatever page id a caller (a damaged B-tree reference, say) hands it.
/// The owning storage creates and owns the manager; it is not disposable on its own.
/// </remarks>
public sealed class StoragePageManager
{
    private readonly StorageStream _stream;
    private readonly StorageBufferPool _bufferPool;
    private readonly StorageFreeSpaceMap _freeSpaceMap;

    internal StoragePageManager(StorageStream stream, StorageBufferPool bufferPool, StorageFreeSpaceMap freeSpaceMap)
    {
        _stream = stream;
        _bufferPool = bufferPool;
        _freeSpaceMap = freeSpaceMap;

        // Never allocated: the file header is not a data page.
        _freeSpaceMap.MarkAllocated((PageId)0L);
    }

    /// <summary>
    /// Gets the total number of pages currently allocated in the storage file.
    /// </summary>
    public long PageCount => _freeSpaceMap.TotalPageCount;

    /// <summary>
    /// Gets the number of free (unallocated) pages available for use.
    /// </summary>
    public long FreePageCount => _freeSpaceMap.FreePageCount;

    /// <summary>
    /// Allocates a new page of the specified type from the storage file.
    /// </summary>
    /// <param name="type">The type of page to allocate.</param>
    /// <returns>A handle to the newly allocated page, pinned in the buffer pool.</returns>
    public unsafe StoragePageHandle AllocatePage(PageType type)
    {
        var pageId = _freeSpaceMap.Allocate();

        // Pin the page without reading it: every byte is cleared below, so the free page's
        // old content is never needed, and a free page whose last write a crash tore (an
        // unjournaled checkpoint anchor page) must not fail its checksum here (#1251).
        var handle = _bufferPool.PinForOverwrite(pageId, _stream);

        // Now extend the stream to accommodate the new page. Grow only, and under the pool
        // lock that every page read and write-back holds: a concurrent allocation of a
        // higher page may already have extended it (a shorter length would cut that page
        // off), and an in-memory stream replaces its array when it grows, which would drop
        // a write-back landing in the old array.
        _bufferPool.EnsureLength(_stream, ((long)pageId + 1) * Page.Size);

        // Initialize the fresh page (local copy shares the same pointer). The clear spans
        // the pool buffer's fixed size, never a length read from the page's own header.
        //
        // Invariant A (#1253): allocation zeroes the page LSN, with every other byte. Zero is at
        // or below the redo point, so the allocating transaction's first touch journals the page's
        // full image, initialization included. A page freed and reallocated within one checkpoint
        // interval otherwise kept the LSN of its free, its next delta chained onto the freed page,
        // and the initialization done before the touch (type, owner tag, slotted header) was in
        // no record: recovery would rebuild the free page plus the delta, a page that never existed.
        var page = handle.Page;
        new Span<byte>(page.Pointer, Page.Size).Clear();
        page.Id = (long)pageId;
        page.Type = type;
        handle.MarkDirty();

        // The clear is outside the journal: an allocating storage transaction images the page at
        // its touch, initialization included (invariant A), and anything else allocating is the
        // storage itself (its first page, an anchor page) or a raw caller.
        WroteOutsideJournal?.Invoke((long)pageId);

        return handle;
    }

    /// <summary>
    /// Invoked with a page this manager rewrites outside any storage transaction (<see cref="AllocatePage"/>,
    /// <see cref="FreePage"/>), so the owning storage's debug consistency check stops comparing the
    /// page with its journal records and exempts it from its audit of changes made outside a
    /// transaction until the next checkpoint writes it.
    /// </summary>
    internal Action<long>? WroteOutsideJournal;

    /// <summary>
    /// Returns a page to the free space map, making it available for reuse.
    /// </summary>
    /// <param name="pageId">The identifier of the page to free.</param>
    /// <exception cref="StorageIOException">The page is page 0, the file header, which is never allocated or freed.</exception>
    /// <remarks>
    /// The free is not journaled: the page is rewritten as <see cref="PageType.Free"/> and written
    /// back at once, outside any storage transaction. A crash before the next checkpoint therefore
    /// rebuilds the page from its journal records, as it stood before the free. Storage
    /// transactions free pages through the storage's record operations, which are journaled.
    /// </remarks>
    public unsafe void FreePage(PageId pageId)
    {
        ThrowIfHeaderPage(pageId);
        WroteOutsideJournal?.Invoke((long)pageId);

        // Stamp the page as free on disk so the free-space map can be rebuilt from
        // page headers when the file is reopened, then return it to the map.
        using (var handle = _bufferPool.Pin(pageId, _stream))
        {
            var page = handle.Page;
            new Span<byte>(page.Pointer, Page.Size).Clear();
            page.Id = (long)pageId;
            page.Type = PageType.Free;
            handle.MarkDirty();
        }

        _bufferPool.Evict(pageId, _stream);
        _freeSpaceMap.Free(pageId);
    }

    /// <summary>
    /// Pins an allocated page the caller rewrites completely, without reading its current
    /// bytes from the stream (see <see cref="StorageBufferPool.PinForOverwrite"/>).
    /// </summary>
    /// <param name="pageId">The allocated page to pin.</param>
    /// <returns>A handle on the pinned page; its content is undefined until the caller writes it.</returns>
    internal StoragePageHandle PinForOverwrite(PageId pageId)
    {
        ThrowIfHeaderPage(pageId);

        if (!_freeSpaceMap.IsAllocated(pageId))
        {
            throw new StorageIOException($"Page {(long)pageId} is not allocated.");
        }

        return _bufferPool.PinForOverwrite(pageId, _stream);
    }

    /// <summary>
    /// Retrieves a page by its identifier. The page is loaded from the buffer pool
    /// if cached, or read from the storage stream if not.
    /// </summary>
    /// <param name="pageId">The identifier of the page to retrieve.</param>
    /// <returns>A handle to the page, pinned in the buffer pool.</returns>
    /// <exception cref="StorageIOException">
    /// The page is not allocated, or it is page 0, the file header, which never enters the buffer pool.
    /// </exception>
    public StoragePageHandle GetPage(PageId pageId)
    {
        ThrowIfHeaderPage(pageId);

        if (!_freeSpaceMap.IsAllocated(pageId))
        {
            throw new StorageIOException($"Page {(long)pageId} is not allocated.");
        }

        return _bufferPool.Pin(pageId, _stream);
    }

    private static void ThrowIfHeaderPage(PageId pageId)
    {
        if ((long)pageId == 0L)
        {
            throw new StorageIOException(
                "Page 0 is the file header and is not accessible as a data page: the storage writes its header slots outside the buffer pool.");
        }
    }

    /// <summary>
    /// Flushes a specific dirty page to the underlying storage stream.
    /// </summary>
    /// <param name="pageId">The identifier of the page to flush.</param>
    public void FlushPage(PageId pageId)
    {
        _bufferPool.FlushPage(pageId, _stream);
    }

    /// <summary>
    /// Flushes a specific dirty page to the underlying storage stream asynchronously.
    /// </summary>
    /// <param name="pageId">The identifier of the page to flush.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>A task representing the asynchronous flush operation.</returns>
    public ValueTask FlushPageAsync(PageId pageId, CancellationToken cancellationToken = default)
    {
        FlushPage(pageId);
        return default;
    }

    /// <summary>
    /// Flushes all dirty pages to the underlying storage stream.
    /// </summary>
    public void FlushAll()
    {
        _bufferPool.FlushAll(_stream);
    }

    /// <summary>
    /// Flushes all dirty pages to the underlying storage stream asynchronously.
    /// </summary>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>A task representing the asynchronous flush operation.</returns>
    public ValueTask FlushAllAsync(CancellationToken cancellationToken = default)
    {
        FlushAll();
        return default;
    }
}
