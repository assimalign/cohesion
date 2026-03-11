using System;

namespace Assimalign.Cohesion.Database.Storage;

/// <summary>
/// Tracks which pages in a storage file are allocated versus free.
/// Provides allocation and deallocation of page identifiers without
/// performing any I/O—page I/O is the responsibility of <see cref="IStoragePageManager"/>.
/// </summary>
/// <remarks>
/// The free space map is typically backed by one or more bitmap pages within the storage file.
/// All database models (SQL, Document, KeyValuePair, Graph) share the same free space tracking
/// mechanism since they all operate on the same fixed-size page format.
/// </remarks>
public interface IStorageFreeSpaceMap
{
    /// <summary>
    /// Gets the total number of pages tracked by this free space map.
    /// </summary>
    long TotalPageCount { get; }

    /// <summary>
    /// Gets the number of pages currently marked as free.
    /// </summary>
    long FreePageCount { get; }

    /// <summary>
    /// Allocates the next available free page identifier.
    /// </summary>
    /// <returns>The identifier of the newly allocated page.</returns>
    /// <exception cref="StorageException">Thrown when no free pages are available.</exception>
    PageId Allocate();

    /// <summary>
    /// Marks the specified page as free, making it available for future allocation.
    /// </summary>
    /// <param name="pageId">The identifier of the page to free.</param>
    void Free(PageId pageId);

    /// <summary>
    /// Determines whether the specified page is currently allocated.
    /// </summary>
    /// <param name="pageId">The identifier of the page to check.</param>
    /// <returns><c>true</c> if the page is allocated; otherwise, <c>false</c>.</returns>
    bool IsAllocated(PageId pageId);
}
