using System;

namespace Assimalign.Cohesion.Database.Storage;

using Assimalign.Cohesion.Database.Storage.Units;

/// <summary>
/// Provides a reference to a page pinned in the buffer pool.
/// Disposing the handle releases the pin, allowing the page to be evicted.
/// </summary>
internal sealed class StoragePageHandle : IStoragePageHandle
{
    private readonly StorageBufferPool _pool;
    internal readonly StorageBufferPool.BufferEntry Entry;
    private bool _disposed;

    internal StoragePageHandle(PageId pageId, StorageBufferPool.BufferEntry entry, StorageBufferPool pool)
    {
        Id = pageId;
        Entry = entry;
        _pool = pool;
    }

    /// <inheritdoc />
    public PageId Id { get; }

    /// <inheritdoc />
    public Page Page => Entry.Page;

    /// <inheritdoc />
    public bool IsDirty => Entry.IsDirty;

    /// <inheritdoc />
    public int PinCount => Entry.PinCount;

    /// <inheritdoc />
    public void MarkDirty() => Entry.IsDirty = true;

    /// <inheritdoc />
    public void Dispose()
    {
        if (!_disposed)
        {
            _pool.Unpin(Id);
            _disposed = true;
        }
    }
}
