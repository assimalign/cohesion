using System;
using System.Threading;

namespace Assimalign.Cohesion.Database.Storage;

using Assimalign.Cohesion.Database.Storage.Internal;
using Assimalign.Cohesion.Database.Storage.Units;

/// <summary>
/// A reference to a page that is pinned in the buffer pool. The page remains pinned
/// (protected from eviction) for the lifetime of the handle; disposing the handle releases
/// the pin.
/// </summary>
/// <remarks>
/// Handles follow the RAII pattern: acquire one through <see cref="StoragePageManager.GetPage"/>,
/// <see cref="StoragePageManager.AllocatePage"/> or a storage transaction's page operations, and
/// release it by calling <see cref="Dispose"/>. While a handle is alive, the underlying page
/// buffer is guaranteed to remain valid. Page access sits on the per-page hot path, so the type
/// is sealed and every call on it is direct.
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
public sealed class StoragePageHandle : IDisposable
{
    private readonly StorageBufferPool _pool;
    internal readonly StorageBufferPool.BufferEntry Entry;
    private int _disposed;

    internal StoragePageHandle(PageId pageId, StorageBufferPool.BufferEntry entry, StorageBufferPool pool)
    {
        Id = pageId;
        Entry = entry;
        _pool = pool;
    }

    /// <summary>
    /// Gets the identifier of the pinned page.
    /// </summary>
    public PageId Id { get; }

    /// <summary>
    /// Gets the underlying <see cref="Units.Page"/> struct providing direct access to the page buffer.
    /// </summary>
    /// <remarks>
    /// Valid only while the handle is undisposed: once the pin is released the pool may
    /// load another page into the same buffer. Debug builds of the shared pool throw
    /// <see cref="ObjectDisposedException"/> when the page is read through a disposed
    /// handle; release builds do not check.
    /// </remarks>
    public Page Page
    {
        get
        {
#if DEBUG
            // After its pin is released the entry may already hold another page: a
            // reference through a released handle is a use-after-unpin.
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
#endif
            return Entry.Page;
        }
    }

    /// <summary>
    /// Gets a value indicating whether the page has been modified since it was loaded or last flushed.
    /// </summary>
    public bool IsDirty => Entry.IsDirty;

    /// <summary>
    /// Gets the current pin count for this page. A page cannot be evicted while
    /// its pin count is greater than zero.
    /// </summary>
    public int PinCount => Entry.PinCount;

    /// <summary>
    /// Marks the page as dirty, indicating that it has been modified and must be
    /// flushed to the storage stream before it can be evicted.
    /// </summary>
    public void MarkDirty() => Entry.MarkDirty();

    /// <summary>
    /// Releases the pin. Exactly one release happens per handle, even when two threads dispose
    /// it at once.
    /// </summary>
    public void Dispose()
    {
        // Exactly one release per handle, even when two threads dispose it at once.
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            _pool.Unpin(Entry);
        }
    }
}
