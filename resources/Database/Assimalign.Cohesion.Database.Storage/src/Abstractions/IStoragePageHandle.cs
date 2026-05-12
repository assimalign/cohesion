using System;

namespace Assimalign.Cohesion.Database.Storage;

using Assimalign.Cohesion.Database.Storage.Units;

/// <summary>
/// Represents a reference to a page that is pinned in the buffer pool.
/// The page remains pinned (protected from eviction) for the lifetime of the handle.
/// Disposing the handle releases the pin.
/// </summary>
/// <remarks>
/// Handles follow the RAII pattern: acquire via <see cref="IStoragePageManager.GetPage"/>
/// or <see cref="IStoragePageManager.AllocatePage"/>, and release by calling <see cref="IDisposable.Dispose"/>.
/// While a handle is alive, the underlying page buffer is guaranteed to remain valid.
///
/// <example>
/// <code>
/// using var handle = pageManager.GetPage(pageId);
/// var page = handle.Page;
/// // Read or write through the page...
/// handle.MarkDirty(); // Signal that the page was modified
/// </code>
/// </example>
/// </remarks>
public interface IStoragePageHandle : IDisposable
{
    /// <summary>
    /// Gets the identifier of the pinned page.
    /// </summary>
    PageId Id { get; }

    /// <summary>
    /// Gets the underlying <see cref="Units.Page"/> struct providing direct access to the page buffer.
    /// </summary>
    Page Page { get; }

    /// <summary>
    /// Gets a value indicating whether the page has been modified since it was loaded or last flushed.
    /// </summary>
    bool IsDirty { get; }

    /// <summary>
    /// Gets the current pin count for this page. A page cannot be evicted while
    /// its pin count is greater than zero.
    /// </summary>
    int PinCount { get; }

    /// <summary>
    /// Marks the page as dirty, indicating that it has been modified and must be
    /// flushed to the storage stream before it can be evicted.
    /// </summary>
    void MarkDirty();
}
