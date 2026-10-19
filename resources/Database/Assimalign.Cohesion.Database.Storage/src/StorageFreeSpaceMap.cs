using System;
using System.Collections.Generic;

namespace Assimalign.Cohesion.Database.Storage;

/// <summary>
/// Tracks which pages in a storage file are allocated versus free using an in-memory
/// free list. New pages are appended; freed pages are recycled in first-freed order.
/// It performs no I/O: page I/O is the responsibility of <see cref="StoragePageManager"/>.
/// </summary>
/// <remarks>
/// <para>
/// The map is rebuilt when a storage file is opened by scanning page headers: pages
/// stamped <see cref="PageType.Free"/> return to the free list. A page freed but not
/// yet flushed when the process stops therefore reappears as allocated after reopen —
/// a safe leak (the page is unreachable but never handed out twice), never corruption.
/// </para>
/// <para>
/// Every member is serialized by one lock. Allocation, commit-time frees and scans'
/// allocation checks run on different threads — concurrent storage transactions, and
/// readers beside an engine's single writer — and an unsynchronized allocator can hand
/// the same page to two transactions.
/// </para>
/// </remarks>
public sealed class StorageFreeSpaceMap
{
    private readonly object _sync = new();
    private readonly Queue<long> _freeQueue = new();
    private readonly HashSet<long> _freeSet = new();
    private long _nextPageId;

    internal StorageFreeSpaceMap()
    {
        _nextPageId = 0;
    }

    /// <summary>
    /// Invoked with a page each time <see cref="Free"/> puts it on the free list, on the freeing
    /// thread, once the map's lock is released: the instant another allocation can take the
    /// page. This assembly's tests use it to allocate at that instant.
    /// </summary>
    /// <remarks>
    /// The freeing caller may hold storage locks (a commit releases its pages under the storage's
    /// transaction lock), so a handler must not wait on another thread that takes them.
    /// </remarks>
    internal Action<PageId>? Freed;

    /// <summary>
    /// Gets the total number of pages tracked by this free space map.
    /// </summary>
    public long TotalPageCount
    {
        get
        {
            lock (_sync)
            {
                return _nextPageId;
            }
        }
    }

    /// <summary>
    /// Gets the number of pages currently marked as free.
    /// </summary>
    public long FreePageCount
    {
        get
        {
            lock (_sync)
            {
                return _freeSet.Count;
            }
        }
    }

    /// <summary>
    /// Allocates the next available free page identifier.
    /// </summary>
    /// <returns>The identifier of the newly allocated page.</returns>
    public PageId Allocate()
    {
        lock (_sync)
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
    }

    /// <summary>
    /// Marks the specified page as free, making it available for future allocation.
    /// </summary>
    /// <param name="pageId">The identifier of the page to free.</param>
    /// <exception cref="StorageIOException">The page was never allocated.</exception>
    public void Free(PageId pageId)
    {
        long id = (long)pageId;
        bool added;

        lock (_sync)
        {
            if (id >= _nextPageId)
            {
                throw new StorageIOException($"Cannot free page {id}: the page was never allocated.");
            }

            added = _freeSet.Add(id);
            if (added)
            {
                _freeQueue.Enqueue(id);
            }
        }

        if (added)
        {
            Freed?.Invoke(pageId);
        }
    }

    /// <summary>
    /// Determines whether the specified page is currently allocated.
    /// </summary>
    /// <param name="pageId">The identifier of the page to check.</param>
    /// <returns><c>true</c> if the page is allocated; otherwise, <c>false</c>.</returns>
    public bool IsAllocated(PageId pageId)
    {
        long id = (long)pageId;

        lock (_sync)
        {
            return id < _nextPageId && !_freeSet.Contains(id);
        }
    }

    /// <summary>
    /// Marks a page as already allocated during storage initialization.
    /// This advances the internal page counter without adding to the free list.
    /// </summary>
    /// <param name="pageId">The page to mark as allocated.</param>
    internal void MarkAllocated(PageId pageId)
    {
        long id = (long)pageId;

        lock (_sync)
        {
            if (id >= _nextPageId)
            {
                _nextPageId = id + 1;
            }

            _freeSet.Remove(id);
        }
    }

    /// <summary>
    /// Marks a page as free during storage initialization (a page whose header is
    /// stamped <see cref="PageType.Free"/> on disk).
    /// </summary>
    /// <param name="pageId">The page to mark as free.</param>
    internal void MarkFree(PageId pageId)
    {
        long id = (long)pageId;

        lock (_sync)
        {
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
}
