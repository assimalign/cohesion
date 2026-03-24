using System;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Database.Storage;

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

        // Now extend the stream to accommodate the new page
        long requiredLength = ((long)pageId + 1) * Page.Size;
        if (_stream.Length < requiredLength)
        {
            _stream.SetLength(requiredLength);
        }

        // Initialize the fresh page (local copy shares the same pointer)
        var page = handle.Page;
        page.AsSpan().Clear();
        page.Id = (long)pageId;
        page.Type = type;
        handle.MarkDirty();

        return handle;
    }

    /// <inheritdoc />
    public void FreePage(PageId pageId)
    {
        _bufferPool.Evict(pageId, _stream);
        _freeSpaceMap.Free(pageId);
    }

    /// <inheritdoc />
    public IStoragePageHandle GetPage(PageId pageId)
    {
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
