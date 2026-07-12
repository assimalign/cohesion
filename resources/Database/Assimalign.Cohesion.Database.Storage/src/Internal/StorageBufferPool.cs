using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace Assimalign.Cohesion.Database.Storage;

using Assimalign.Cohesion.Database.Storage.Internal;
using Assimalign.Cohesion.Database.Storage.Units;

/// <summary>
/// An in-memory page cache that pins page buffers to prevent garbage collection
/// and supports pin-counted, least-recently-used eviction with buffer reuse.
/// </summary>
/// <remarks>
/// <para>
/// Eviction policy: when the pool is at capacity, the least recently used page whose
/// pin count is zero is evicted (written back first when dirty). Pinned pages are never
/// evicted; if every resident page is pinned the pool refuses the new page loudly
/// rather than silently exceeding its budget.
/// </para>
/// <para>
/// Buffer reuse: evicted entries return their pinned 8 KiB buffers to a recycle stack,
/// so a pool under steady load reaches its capacity in GC-pinned allocations and stays
/// there — page churn does not allocate.
/// </para>
/// <para>
/// Integrity: every page loaded from the storage stream is verified against its
/// header checksum, and every write-back stamps a fresh checksum, so corruption is
/// detected on the read path rather than propagating silently.
/// </para>
/// </remarks>
internal sealed unsafe class StorageBufferPool : IStorageBufferPool
{
    private readonly Dictionary<long, BufferEntry> _entries = new();
    private readonly LinkedList<long> _accessOrder = new(); // head = least recently used
    private readonly Stack<BufferEntry> _recycled = new();
    private readonly object _syncRoot = new();
    private readonly int _capacity;

    internal StorageBufferPool(int capacity)
    {
        if (capacity < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(capacity), "Buffer pool capacity must be at least one page.");
        }

        _capacity = capacity;
    }

    /// <inheritdoc />
    public int Capacity => _capacity;

    /// <inheritdoc />
    public int Count
    {
        get
        {
            lock (_syncRoot)
            {
                return _entries.Count;
            }
        }
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
                Touch(entry);
                return new StoragePageHandle(pageId, entry, this);
            }

            if (_entries.Count >= _capacity)
            {
                EvictOneLocked(stream);
            }

            entry = TakeEntryLocked();

            if (id * Page.Size < stream.Length)
            {
                try
                {
                    stream.ReadPage(pageId, entry.Buffer);
                    PageChecksum.Verify(entry.Buffer, pageId);
                }
                catch
                {
                    // Do not cache a page that failed to load or verify.
                    RecycleLocked(entry);
                    throw;
                }
            }
            else
            {
                Array.Clear(entry.Buffer);
            }

            entry.PinCount = 1;
            _entries[id] = entry;
            entry.Node = _accessOrder.AddLast(id);

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
                Touch(entry);
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
                WriteBack(stream, pageId, entry);
            }

            RemoveLocked(id, entry);
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
                    WriteBack(stream, (PageId)kvp.Key, kvp.Value);
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
                WriteBack(stream, pageId, entry);
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
            _accessOrder.Clear();

            while (_recycled.Count > 0)
            {
                _recycled.Pop().Release();
            }
        }
    }

    /// <summary>
    /// Moves the entry to the most-recently-used position.
    /// </summary>
    private void Touch(BufferEntry entry)
    {
        if (entry.Node is not null)
        {
            _accessOrder.Remove(entry.Node);
            _accessOrder.AddLast(entry.Node);
        }
    }

    /// <summary>
    /// Evicts the least recently used unpinned page, writing it back first when dirty.
    /// </summary>
    private void EvictOneLocked(StorageStream stream)
    {
        for (var node = _accessOrder.First; node is not null; node = node.Next)
        {
            var entry = _entries[node.Value];

            if (entry.PinCount > 0)
            {
                continue;
            }

            if (entry.IsDirty)
            {
                WriteBack(stream, (PageId)node.Value, entry);
            }

            RemoveLocked(node.Value, entry);
            return;
        }

        throw new StorageIOException("Buffer pool is full and all pages are pinned.");
    }

    /// <summary>
    /// Takes a recycled entry when one is available, otherwise allocates a fresh
    /// pinned buffer.
    /// </summary>
    private BufferEntry TakeEntryLocked()
    {
        if (_recycled.Count > 0)
        {
            var recycledEntry = _recycled.Pop();
            recycledEntry.IsDirty = false;
            recycledEntry.PinCount = 0;
            recycledEntry.Node = null;
            return recycledEntry;
        }

        return new BufferEntry(new byte[Page.Size]);
    }

    private void RemoveLocked(long id, BufferEntry entry)
    {
        if (entry.Node is not null)
        {
            _accessOrder.Remove(entry.Node);
            entry.Node = null;
        }

        _entries.Remove(id);
        RecycleLocked(entry);
    }

    private void RecycleLocked(BufferEntry entry)
    {
        entry.IsDirty = false;
        entry.PinCount = 0;
        entry.Node = null;
        _recycled.Push(entry);
    }

    /// <summary>
    /// Stamps the page checksum and writes the buffer to the stream, clearing the dirty flag.
    /// </summary>
    private static void WriteBack(StorageStream stream, PageId pageId, BufferEntry entry)
    {
        PageChecksum.Stamp(entry.Buffer);
        stream.WritePage(pageId, entry.Buffer);
        entry.IsDirty = false;
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
        public LinkedListNode<long>? Node;

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
