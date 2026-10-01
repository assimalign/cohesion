using System;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Database.Storage.Internal;

using Assimalign.Cohesion.Database.Storage.Units;

/// <summary>
/// Coordinates page-level operations by managing the buffer pool, free space map,
/// and storage stream together.
/// </summary>
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
    }

    /// <inheritdoc />
    public long PageCount => _freeSpaceMap.TotalPageCount;

    /// <inheritdoc />
    public long FreePageCount => _freeSpaceMap.FreePageCount;

    /// <inheritdoc />
    public unsafe IStoragePageHandle AllocatePage(PageType type)
    {
        var pageId = _freeSpaceMap.Allocate();

        // Pin the page in the buffer pool (will allocate a fresh buffer
        // since the page doesn't exist in the stream yet)
        var handle = _bufferPool.Pin(pageId, _stream);

        // Now extend the stream to accommodate the new page. Grow only, and under the pool
        // lock that every page read and write-back holds: a concurrent allocation of a
        // higher page may already have extended it (a shorter length would cut that page
        // off), and an in-memory stream replaces its array when it grows, which would drop
        // a write-back landing in the old array.
        _bufferPool.EnsureLength(_stream, ((long)pageId + 1) * Page.Size);

        // Initialize the fresh page (local copy shares the same pointer). The clear spans
        // the pool buffer's fixed size, never a length read from the page's own header.
        var page = handle.Page;
        new Span<byte>(page.Pointer, Page.Size).Clear();
        page.Id = (long)pageId;
        page.Type = type;
        handle.MarkDirty();

        return handle;
    }

    /// <inheritdoc />
    public unsafe void FreePage(PageId pageId)
    {
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

    /// <inheritdoc />
    public IStoragePageHandle GetPage(PageId pageId)
    {
        if (!_freeSpaceMap.IsAllocated(pageId))
        {
            throw new StorageIOException($"Page {(long)pageId} is not allocated.");
        }

        return _bufferPool.Pin(pageId, _stream);
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
