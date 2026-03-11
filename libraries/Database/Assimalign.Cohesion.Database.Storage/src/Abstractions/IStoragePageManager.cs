using System;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Database.Storage;

/// <summary>
/// Coordinates page-level operations for a storage file including allocation,
/// deallocation, and I/O. This is the primary interface that all database models
/// (SQL, Document, KeyValuePair, Graph) use for managing their underlying page storage.
/// </summary>
/// <remarks>
/// The page manager sits between the buffer pool and the physical storage stream.
/// It provides a unified API for:
/// <list type="bullet">
///   <item>Allocating new pages from the free space map</item>
///   <item>Freeing pages that are no longer needed</item>
///   <item>Retrieving pages by ID (from cache or disk)</item>
///   <item>Flushing dirty pages back to the storage stream</item>
/// </list>
/// </remarks>
public interface IStoragePageManager : IAsyncDisposable, IDisposable
{
    /// <summary>
    /// Gets the total number of pages currently allocated in the storage file.
    /// </summary>
    long PageCount { get; }

    /// <summary>
    /// Gets the number of free (unallocated) pages available for use.
    /// </summary>
    long FreePageCount { get; }

    /// <summary>
    /// Allocates a new page of the specified type from the storage file.
    /// </summary>
    /// <param name="type">The type of page to allocate.</param>
    /// <returns>A handle to the newly allocated page, pinned in the buffer pool.</returns>
    IStoragePageHandle AllocatePage(PageType type);

    /// <summary>
    /// Returns a page to the free space map, making it available for reuse.
    /// </summary>
    /// <param name="pageId">The identifier of the page to free.</param>
    void FreePage(PageId pageId);

    /// <summary>
    /// Retrieves a page by its identifier. The page is loaded from the buffer pool
    /// if cached, or read from the storage stream if not.
    /// </summary>
    /// <param name="pageId">The identifier of the page to retrieve.</param>
    /// <returns>A handle to the page, pinned in the buffer pool.</returns>
    IStoragePageHandle GetPage(PageId pageId);

    /// <summary>
    /// Flushes a specific dirty page to the underlying storage stream.
    /// </summary>
    /// <param name="pageId">The identifier of the page to flush.</param>
    void FlushPage(PageId pageId);

    /// <summary>
    /// Flushes a specific dirty page to the underlying storage stream asynchronously.
    /// </summary>
    /// <param name="pageId">The identifier of the page to flush.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>A task representing the asynchronous flush operation.</returns>
    ValueTask FlushPageAsync(PageId pageId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Flushes all dirty pages to the underlying storage stream.
    /// </summary>
    void FlushAll();

    /// <summary>
    /// Flushes all dirty pages to the underlying storage stream asynchronously.
    /// </summary>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>A task representing the asynchronous flush operation.</returns>
    ValueTask FlushAllAsync(CancellationToken cancellationToken = default);
}
