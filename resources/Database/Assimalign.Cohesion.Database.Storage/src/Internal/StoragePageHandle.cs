using System;
using System.Threading;

namespace Assimalign.Cohesion.Database.Storage.Internal;

using Assimalign.Cohesion.Database.Storage.Units;

/// <summary>
/// Provides a reference to a page pinned in the buffer pool.
/// Disposing the handle releases the pin, allowing the page to be evicted.
/// </summary>
internal sealed class StoragePageHandle : IStoragePageHandle
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

    /// <inheritdoc />
    public PageId Id { get; }

    /// <inheritdoc />
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

    /// <inheritdoc />
    public bool IsDirty => Entry.IsDirty;

    /// <inheritdoc />
    public int PinCount => Entry.PinCount;

    /// <inheritdoc />
    public void MarkDirty() => Entry.MarkDirty();

    /// <inheritdoc />
    public void Dispose()
    {
        // Exactly one release per handle, even when two threads dispose it at once.
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            _pool.Unpin(Entry);
        }
    }
}
