using System;
using System.Collections.Generic;

namespace Assimalign.Cohesion.Database.Storage;

/// <summary>
/// Tracks which pages in a storage file are allocated versus free using an in-memory
/// free list. New pages are appended; freed pages are recycled in first-freed order.
/// </summary>
/// <remarks>
/// The map is rebuilt when a storage file is opened by scanning page headers: pages
/// stamped <see cref="PageType.Free"/> return to the free list. A page freed but not
/// yet flushed when the process stops therefore reappears as allocated after reopen —
/// a safe leak (the page is unreachable but never handed out twice), never corruption.
/// </remarks>
internal sealed class StorageFreeSpaceMap : IStorageFreeSpaceMap
{
    private long _nextPageId;
    private readonly Queue<long> _freeQueue = new();
    private readonly HashSet<long> _freeSet = new();

    internal StorageFreeSpaceMap()
    {
        _nextPageId = 0;
    }

    /// <inheritdoc />
    public long TotalPageCount => _nextPageId;

    /// <inheritdoc />
    public long FreePageCount => _freeSet.Count;

    /// <inheritdoc />
    public PageId Allocate()
    {
        while (_freeQueue.Count > 0)
        {
            long recycled = _freeQueue.Dequeue();

            // Entries may be stale when MarkAllocated reclaimed the id during reopen.
            if (_freeSet.Remove(recycled))
            {
                return (PageId)recycled;
            }
        }

        return (PageId)_nextPageId++;
    }

    /// <inheritdoc />
    public void Free(PageId pageId)
    {
        long id = (long)pageId;

        if (id >= _nextPageId)
        {
            throw new StorageIOException($"Cannot free page {id}: the page was never allocated.");
        }

        if (_freeSet.Add(id))
        {
            _freeQueue.Enqueue(id);
        }
    }

    /// <inheritdoc />
    public bool IsAllocated(PageId pageId)
    {
        long id = (long)pageId;
        return id < _nextPageId && !_freeSet.Contains(id);
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

        _freeSet.Remove(id);
    }

    /// <summary>
    /// Marks a page as free during storage initialization (a page whose header is
    /// stamped <see cref="PageType.Free"/> on disk).
    /// </summary>
    /// <param name="pageId">The page to mark as free.</param>
    internal void MarkFree(PageId pageId)
    {
        long id = (long)pageId;

        if (id >= _nextPageId)
        {
            _nextPageId = id + 1;
        }

        if (_freeSet.Add(id))
        {
            _freeQueue.Enqueue(id);
        }
    }
}
