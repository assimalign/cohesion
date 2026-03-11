using System;
using System.Collections.Generic;

namespace Assimalign.Cohesion.Database.Storage;

/// <summary>
/// Tracks which pages in a storage file are allocated versus free using an in-memory
/// free list. New pages are appended; freed pages are recycled on the next allocation.
/// </summary>
internal sealed class StorageFreeSpaceMap : IStorageFreeSpaceMap
{
    private long _nextPageId;
    private long _allocatedCount;
    private readonly Queue<long> _freeList = new();

    internal StorageFreeSpaceMap()
    {
        _nextPageId = 0;
        _allocatedCount = 0;
    }

    /// <inheritdoc />
    public long TotalPageCount => _nextPageId;

    /// <inheritdoc />
    public long FreePageCount => _freeList.Count;

    /// <inheritdoc />
    public PageId Allocate()
    {
        long id;

        if (_freeList.Count > 0)
        {
            id = _freeList.Dequeue();
        }
        else
        {
            id = _nextPageId++;
        }

        _allocatedCount++;
        return (PageId)id;
    }

    /// <inheritdoc />
    public void Free(PageId pageId)
    {
        _freeList.Enqueue((long)pageId);
        _allocatedCount--;
    }

    /// <inheritdoc />
    public bool IsAllocated(PageId pageId)
    {
        long id = (long)pageId;
        return id < _nextPageId && !_freeList.Contains(id);
    }

    /// <summary>
    /// Marks a page as already allocated during storage initialization.
    /// This advances the internal page counter without adding to the free list.
    /// </summary>
    /// <param name="pageId">The page to mark as allocated.</param>
    internal void MarkAllocated(PageId pageId)
    {
        long id = (long)pageId;

        if (id >= _nextPageId)
        {
            _nextPageId = id + 1;
        }

        _allocatedCount++;
    }
}
