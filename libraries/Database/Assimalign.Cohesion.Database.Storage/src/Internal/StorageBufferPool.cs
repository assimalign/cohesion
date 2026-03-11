using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace Assimalign.Cohesion.Database.Storage;

using Assimalign.Cohesion.Database.Storage.Units;

/// <summary>
/// An in-memory page cache that pins page buffers to prevent garbage collection
/// and supports pin-counted eviction.
/// </summary>
internal sealed unsafe class StorageBufferPool : IStorageBufferPool
{
    private readonly Dictionary<long, BufferEntry> _entries = new();
    private readonly object _syncRoot = new();
    private readonly int _capacity;

    internal StorageBufferPool(int capacity)
    {
        _capacity = capacity;
    }

    /// <inheritdoc />
    public int Capacity => _capacity;

    /// <inheritdoc />
    public int Count
    {
        get { lock (_syncRoot) return _entries.Count; }
    }

    /// <inheritdoc />
    public IStoragePageHandle Pin(PageId pageId, StorageStream stream)
    {
        lock (_syncRoot)
        {
            long id = (long)pageId;

            if (_entries.TryGetValue(id, out var entry))
            {
                entry.PinCount++;
                return new StoragePageHandle(pageId, entry, this);
            }

            if (_entries.Count >= _capacity)
            {
                EvictOneLocked(stream);
            }

            var buffer = new byte[Page.Size];

            if (id * Page.Size < stream.Length)
            {
                stream.ReadPage(pageId, buffer);
            }

            entry = new BufferEntry(buffer);
            entry.PinCount = 1;
            _entries[id] = entry;

            return new StoragePageHandle(pageId, entry, this);
        }
    }

    /// <inheritdoc />
    public void Unpin(PageId pageId)
    {
        lock (_syncRoot)
        {
            if (_entries.TryGetValue((long)pageId, out var entry) && entry.PinCount > 0)
            {
                entry.PinCount--;
            }
        }
    }

    /// <inheritdoc />
    public bool TryGet(PageId pageId, out IStoragePageHandle? handle)
    {
        lock (_syncRoot)
        {
            if (_entries.TryGetValue((long)pageId, out var entry))
            {
                entry.PinCount++;
                handle = new StoragePageHandle(pageId, entry, this);
                return true;
            }

            handle = null;
            return false;
        }
    }

    /// <inheritdoc />
    public void Evict(PageId pageId, StorageStream stream)
    {
        lock (_syncRoot)
        {
            long id = (long)pageId;

            if (!_entries.TryGetValue(id, out var entry))
            {
                return;
            }

            if (entry.PinCount > 0)
            {
                throw new StorageIOException($"Cannot evict page {id}: page is pinned.");
            }

            if (entry.IsDirty)
            {
                stream.WritePage(pageId, entry.Buffer);
            }

            entry.Release();
            _entries.Remove(id);
        }
    }

    /// <inheritdoc />
    public void FlushAll(StorageStream stream)
    {
        lock (_syncRoot)
        {
            foreach (var kvp in _entries)
            {
                if (kvp.Value.IsDirty)
                {
                    stream.WritePage((PageId)kvp.Key, kvp.Value.Buffer);
                    kvp.Value.IsDirty = false;
                }
            }

            stream.Flush();
        }
    }

    /// <summary>
    /// Flushes a single dirty page to the stream without evicting it.
    /// </summary>
    /// <param name="pageId">The page to flush.</param>
    /// <param name="stream">The stream to write to.</param>
    internal void FlushPage(PageId pageId, StorageStream stream)
    {
        lock (_syncRoot)
        {
            if (_entries.TryGetValue((long)pageId, out var entry) && entry.IsDirty)
            {
                stream.WritePage(pageId, entry.Buffer);
                entry.IsDirty = false;
            }
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        lock (_syncRoot)
        {
            foreach (var entry in _entries.Values)
            {
                entry.Release();
            }

            _entries.Clear();
        }
    }

    private void EvictOneLocked(StorageStream stream)
    {
        long evictKey = -1;
        BufferEntry? evictEntry = null;

        foreach (var kvp in _entries)
        {
            if (kvp.Value.PinCount == 0)
            {
                evictKey = kvp.Key;
                evictEntry = kvp.Value;
                break;
            }
        }

        if (evictEntry == null)
        {
            throw new StorageIOException("Buffer pool is full and all pages are pinned.");
        }

        if (evictEntry.IsDirty)
        {
            stream.WritePage((PageId)evictKey, evictEntry.Buffer);
        }

        evictEntry.Release();
        _entries.Remove(evictKey);
    }

    /// <summary>
    /// Holds a pinned byte buffer and the associated page metadata.
    /// </summary>
    internal sealed unsafe class BufferEntry
    {
        public readonly byte[] Buffer;
        public GCHandle GcHandle;
        public Page Page;
        public int PinCount;
        public bool IsDirty;

        public BufferEntry(byte[] buffer)
        {
            Buffer = buffer;
            GcHandle = GCHandle.Alloc(buffer, GCHandleType.Pinned);
            Page = new Page((byte*)GcHandle.AddrOfPinnedObject());
        }

        public void Release()
        {
            if (GcHandle.IsAllocated)
            {
                GcHandle.Free();
            }
        }
    }
}
