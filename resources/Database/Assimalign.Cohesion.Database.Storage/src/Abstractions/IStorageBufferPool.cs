using System;

namespace Assimalign.Cohesion.Database.Storage;

/// <summary>
/// Manages an in-memory cache of pages with support for pinning and eviction.
/// The buffer pool prevents frequently accessed pages from being re-read from disk
/// and ensures that pages under active use are not evicted.
/// </summary>
/// <remarks>
/// All database models share the same buffer pool infrastructure. The pool uses
/// a pin-counting mechanism: a page can only be evicted when its pin count reaches zero.
/// Pages are marked dirty when modified and must be flushed to disk before eviction.
/// </remarks>
public interface IStorageBufferPool : IDisposable
{
    /// <summary>
    /// Gets the maximum number of pages that can be held in the buffer pool.
    /// </summary>
    int Capacity { get; }

    /// <summary>
    /// Gets the current number of pages resident in the buffer pool.
    /// </summary>
    int Count { get; }

    /// <summary>
    /// Retrieves a page from the pool and increments its pin count.
    /// If the page is not in the pool, it is loaded from the backing storage stream.
    /// </summary>
    /// <param name="pageId">The identifier of the page to pin.</param>
    /// <param name="stream">The storage stream to read from if the page is not cached.</param>
    /// <returns>A handle to the pinned page.</returns>
    IStoragePageHandle Pin(PageId pageId, StorageStream stream);

    /// <summary>
    /// Decrements the pin count for the specified page. When the pin count
    /// reaches zero, the page becomes eligible for eviction.
    /// </summary>
    /// <param name="pageId">The identifier of the page to unpin.</param>
    void Unpin(PageId pageId);

    /// <summary>
    /// Attempts to retrieve a page from the pool without loading it from disk.
    /// </summary>
    /// <param name="pageId">The identifier of the page to look up.</param>
    /// <param name="handle">When this method returns, contains the page handle if the page was found; otherwise, <c>null</c>.</param>
    /// <returns><c>true</c> if the page was found in the pool; otherwise, <c>false</c>.</returns>
    bool TryGet(PageId pageId, out IStoragePageHandle? handle);

    /// <summary>
    /// Forces eviction of a specific page from the pool. The page must not be pinned.
    /// If the page is dirty, it is flushed to disk before eviction.
    /// </summary>
    /// <param name="pageId">The identifier of the page to evict.</param>
    /// <param name="stream">The storage stream to flush to if the page is dirty.</param>
    void Evict(PageId pageId, StorageStream stream);

    /// <summary>
    /// Flushes all dirty pages in the buffer pool to the backing storage stream.
    /// </summary>
    /// <param name="stream">The storage stream to write dirty pages to.</param>
    void FlushAll(StorageStream stream);
}
