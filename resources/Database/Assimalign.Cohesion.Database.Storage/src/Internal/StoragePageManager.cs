using System;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Database.Storage.Internal;

using Assimalign.Cohesion.Database.Storage.Units;

/// <summary>
/// Coordinates page-level operations by managing the buffer pool, free space map,
/// and storage stream together.
/// </summary>
/// <remarks>
/// Page 0 is the file header (storage format 2). The storage rewrites its header slots in place
/// with positional writes that bypass the buffer pool, so a pooled copy of page 0 would be stale
/// after the next header write, and its page checksum is zero, so a stale or damaged copy would
/// never be caught. A write-back of one, or a journaled image of it that recovery replays, would
/// roll both header slots back. The manager therefore reserves page 0 in the free-space map and
/// refuses to pin or free it, whatever page id a caller (a damaged B-tree reference, say) hands it.
/// </remarks>
internal sealed class StoragePageManager : IStoragePageManager
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

    /// <inheritdoc />
    public long PageCount => _freeSpaceMap.TotalPageCount;

    /// <inheritdoc />
    public long FreePageCount => _freeSpaceMap.FreePageCount;

    /// <inheritdoc />
    public unsafe IStoragePageHandle AllocatePage(PageType type)
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

        return handle;
    }

    /// <summary>
    /// Invoked with a page this manager rewrites outside any storage transaction (<see cref="FreePage"/>),
    /// so the owning storage's debug consistency check stops comparing the page with its journal
    /// records.
    /// </summary>
    internal Action<long>? WroteOutsideJournal;

    /// <inheritdoc />
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
    internal IStoragePageHandle PinForOverwrite(PageId pageId)
    {
        ThrowIfHeaderPage(pageId);

        if (!_freeSpaceMap.IsAllocated(pageId))
        {
            throw new StorageIOException($"Page {(long)pageId} is not allocated.");
        }

        return _bufferPool.PinForOverwrite(pageId, _stream);
    }

    /// <inheritdoc />
    public IStoragePageHandle GetPage(PageId pageId)
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

    /// <inheritdoc />
    public void FlushPage(PageId pageId)
    {
        _bufferPool.FlushPage(pageId, _stream);
    }

    /// <inheritdoc />
    public ValueTask FlushPageAsync(PageId pageId, CancellationToken cancellationToken = default)
    {
        FlushPage(pageId);
        return default;
    }

    /// <inheritdoc />
    public void FlushAll()
    {
        _bufferPool.FlushAll(_stream);
    }

    /// <inheritdoc />
    public ValueTask FlushAllAsync(CancellationToken cancellationToken = default)
    {
        FlushAll();
        return default;
    }

    /// <inheritdoc />
    public void Dispose()
    {
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        return default;
    }
}
