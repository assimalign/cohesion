using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;

namespace Assimalign.Cohesion.Database.Storage.Internal;

using Assimalign.Cohesion.Database.Storage.Units;

/// <summary>
/// An in-memory page cache over buffers that never move and supports pin-counted,
/// least-recently-used eviction with buffer reuse.
/// </summary>
/// <remarks>
/// <para>
/// Eviction policy: when the pool is at capacity, the least recently used page whose
/// pin count is zero is evicted (written back first when dirty). Pinned pages are never
/// evicted; if every resident page is pinned the pool refuses the new page loudly
/// rather than silently exceeding its budget.
/// </para>
/// <para>
/// Buffer reuse: evicted entries return their 8 KiB buffers to a recycle stack, so a
/// pool under steady load reaches its capacity in allocations and stays there — page
/// churn does not allocate. Buffers live on the pinned object heap: a raw page pointer
/// stays valid for as long as anything references the buffer, so no pin can be released
/// out from under a live handle, not even by disposing the pool.
/// </para>
/// <para>
/// Integrity: every page loaded from the storage stream is verified against its
/// header checksum, and every write-back stamps a fresh checksum over a private copy of
/// the page, so the bytes that reach the stream always verify — even when a pinned
/// writer is changing the page while it is written.
/// </para>
/// <para>
/// Dirty tracking: every <see cref="StoragePageHandle.MarkDirty"/> advances the entry's
/// modification version, and a write-back records the page clean only up to the version
/// it copied. A change made while the page is being written keeps the page dirty, so the
/// pool can never evict it clean and drop the change.
/// </para>
/// </remarks>
internal sealed unsafe class StorageBufferPool : IDisposable
{
    private readonly Dictionary<long, BufferEntry> _entries = new();
    private readonly LinkedList<long> _accessOrder = new(); // head = least recently used
    private readonly Stack<BufferEntry> _recycled = new();
    private readonly object _syncRoot = new();
    private int _capacity;

    // The image a write-back stamps and writes; guarded by _syncRoot like every write-back.
    private readonly byte[] _writeBackImage = new byte[Page.Size];

    // Scratch set for the structural check; guarded by _syncRoot.
    private readonly HashSet<BufferEntry> _invariantScratch = new(ReferenceEqualityComparer.Instance);
    private bool _disposed;

    /// <summary>
    /// The write-ahead gate: invoked with a page's LSN before the page is written to
    /// the storage stream, so the journal can be made durable up to that LSN first.
    /// Set by the owning storage once its journal exists.
    /// </summary>
    internal Action<long>? WriteAheadGate;

    /// <summary>
    /// Invoked before every write the pool makes to the storage stream — a page write-back or
    /// an extension of the stream — whatever the page's LSN: the owning storage throws from it
    /// once it is offline, so no data write follows a failed durable flush (#1243).
    /// </summary>
    internal Action? WriteGuard;

    /// <summary>
    /// Invoked with a page's id and LSN before every write-back, after <see cref="WriteGuard"/>:
    /// the owning storage's debug consistency check audits that no page reaches the stream with a
    /// change no journal record holds (#1253). Null unless the check is on; it throws to refuse
    /// the write.
    /// </summary>
    internal Action<long, long>? WriteBackAudit;

    /// <summary>
    /// The name of the storage that owns the pool, for its events' <c>database</c> payload
    /// (<see cref="StorageEventSource"/>): set by the storage when it is created or opened, and
    /// empty before that and for a pool nothing owns.
    /// </summary>
    internal string StorageName { get; set; } = string.Empty;

    internal StorageBufferPool(int capacity)
    {
        ValidateCapacity(capacity);
        _capacity = capacity;
    }

    /// <summary>
    /// Gets the maximum number of pages that can be held in the buffer pool.
    /// </summary>
    public int Capacity
    {
        get
        {
            lock (_syncRoot)
            {
                return _capacity;
            }
        }
    }

    /// <summary>
    /// Changes the number of pages the pool may hold. Growing takes effect at once; shrinking
    /// evicts least recently used unpinned pages (writing dirty ones back first, through the
    /// write-ahead gate) until the resident pages fit the new capacity.
    /// </summary>
    /// <param name="capacity">The new capacity in pages; at least one.</param>
    /// <param name="stream">The stream evicted dirty pages are written to.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="capacity"/> is less than one page.</exception>
    /// <exception cref="StorageIOException">
    /// More pages than the new capacity are pinned; the pool is unchanged.
    /// </exception>
    /// <remarks>
    /// When the write-back of an evicted page fails, the failure propagates and the pool keeps
    /// the smallest capacity that still holds every resident page, so its invariants hold.
    /// </remarks>
    internal void Resize(int capacity, StorageStream stream)
    {
        ValidateCapacity(capacity);
        int previous;

        lock (_syncRoot)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            previous = _capacity;

            int pinned = 0;
            foreach (var entry in _entries.Values)
            {
                if (entry.PinCount > 0)
                {
                    pinned++;
                }
            }

            if (pinned > capacity)
            {
                throw new StorageIOException(
                    $"Cannot shrink the buffer pool to {capacity} pages: {pinned} pages are pinned.");
            }

            _capacity = capacity;

            try
            {
                while (_entries.Count > _capacity)
                {
                    EvictOneLocked(stream);
                }
            }
            catch
            {
                _capacity = Math.Max(_capacity, _entries.Count);
                throw;
            }

            AssertInvariantsLocked();
        }

        // Outside the pool lock; an unchanged capacity (an engine applying its option to a pool
        // already that size) is not a resize.
        if (previous != capacity)
        {
            StorageEventSource.Log.BufferPoolResized(this, previous, capacity);
        }
    }

    private static void ValidateCapacity(int capacity)
    {
        if (capacity < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(capacity), "Buffer pool capacity must be at least one page.");
        }
    }

    /// <summary>
    /// Gets the current number of pages resident in the buffer pool.
    /// </summary>
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

    /// <summary>
    /// Retrieves a page from the pool and increments its pin count.
    /// If the page is not in the pool, it is loaded from the backing storage stream.
    /// </summary>
    /// <param name="pageId">The identifier of the page to pin.</param>
    /// <param name="stream">The storage stream to read from if the page is not cached.</param>
    /// <returns>A handle to the pinned page.</returns>
    /// <exception cref="ObjectDisposedException">The pool has been disposed.</exception>
    /// <exception cref="StorageCorruptionException">
    /// The page read from <paramref name="stream"/> failed its checksum, or its header
    /// describes bytes past the end of the page buffer. The page is not cached.
    /// </exception>
    /// <exception cref="StorageIOException">The pool is full and every resident page is pinned, or the stream ended inside the page.</exception>
    public StoragePageHandle Pin(PageId pageId, StorageStream stream)
    {
        lock (_syncRoot)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            long id = (long)pageId;

            if (_entries.TryGetValue(id, out var entry))
            {
                // A hit changes one pin count and one LRU position: check that frame only.
                entry.PinCount++;
                Touch(entry);
                AssertFrameLocked(id, entry);
                return new StoragePageHandle(pageId, entry, this);
            }

            if (_entries.Count >= _capacity)
            {
                EvictOneLocked(stream);
            }

            entry = TakeEntryLocked();

            if (id * Page.Size < stream.Length)
            {
                // Set while the checksum is verified, so a failure there is reported as one.
                bool verifyingChecksum = false;
                try
                {
                    stream.ReadPage(pageId, entry.Buffer);
                    StorageEventSource.Log.CountPageRead();
                    verifyingChecksum = true;
                    PageChecksum.Verify(entry.Buffer, pageId);
                    verifyingChecksum = false;
                    VerifyFitsBuffer(entry.Page, pageId);
                }
                catch
                {
                    // Do not cache a page that failed to load or verify.
                    RecycleLocked(entry);
                    if (verifyingChecksum)
                    {
                        StorageEventSource.Log.PageChecksumFailed(this, pageId);
                    }

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

            // A miss changes the pool's structure (an eviction, a recycled entry, a new
            // resident page): walk all of it.
            AssertInvariantsLocked();

            return new StoragePageHandle(pageId, entry, this);
        }
    }

    /// <summary>
    /// Pins a page every byte of which the caller is about to overwrite: a resident page is
    /// pinned as it is, and a page that is not resident is not read from the stream — it
    /// gets a zeroed buffer instead.
    /// </summary>
    /// <remarks>
    /// Allocation clears a page before anything reads it, so reading and verifying the free
    /// page's old bytes first is wasted I/O, and worse, it refuses the allocation of a free
    /// page whose last write a crash tore. Recovery repairs torn pages only from journal
    /// images, and a page written outside the journal — a freed checkpoint anchor page —
    /// has none. The caller marks the page dirty once it has written it.
    /// </remarks>
    /// <param name="pageId">The page to pin.</param>
    /// <param name="stream">The stream to evict to when the pool is full.</param>
    /// <returns>A handle on the pinned page.</returns>
    internal StoragePageHandle PinForOverwrite(PageId pageId, StorageStream stream)
    {
        lock (_syncRoot)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            long id = (long)pageId;

            if (_entries.TryGetValue(id, out var entry))
            {
                entry.PinCount++;
                Touch(entry);
                AssertFrameLocked(id, entry);
                return new StoragePageHandle(pageId, entry, this);
            }

            if (_entries.Count >= _capacity)
            {
                EvictOneLocked(stream);
            }

            entry = TakeEntryLocked();
            Array.Clear(entry.Buffer);
            entry.PinCount = 1;
            _entries[id] = entry;
            entry.Node = _accessOrder.AddLast(id);
            AssertInvariantsLocked();

            return new StoragePageHandle(pageId, entry, this);
        }
    }

    /// <summary>
    /// Decrements the pin count for the specified page. When the pin count
    /// reaches zero, the page becomes eligible for eviction.
    /// </summary>
    /// <remarks>
    /// Releasing a pin the page does not hold is a caller bug. Debug builds throw
    /// <see cref="InvalidOperationException"/> for it; release builds leave the
    /// pin count at zero. A page that is not resident is ignored.
    /// </remarks>
    /// <param name="pageId">The identifier of the page to unpin.</param>
    public void Unpin(PageId pageId)
    {
        lock (_syncRoot)
        {
            if (_entries.TryGetValue((long)pageId, out var entry))
            {
                UnpinLocked(entry);
            }
        }
    }

    /// <summary>
    /// Releases one pin a handle holds on its own entry. Unpinning the entry rather than
    /// whatever is resident under the page id means a handle can only ever release the
    /// pin it took.
    /// </summary>
    /// <param name="entry">The entry the handle pinned.</param>
    internal void Unpin(BufferEntry entry)
    {
        lock (_syncRoot)
        {
            if (_disposed)
            {
                return;
            }

            AssertResidentLocked(entry);
            UnpinLocked(entry);
        }
    }

    /// <summary>
    /// Attempts to retrieve a page from the pool without loading it from disk.
    /// </summary>
    /// <param name="pageId">The identifier of the page to look up.</param>
    /// <param name="handle">When this method returns, contains the page handle if the page was found; otherwise, <c>null</c>.</param>
    /// <returns><c>true</c> if the page was found in the pool; otherwise, <c>false</c>.</returns>
    /// <exception cref="ObjectDisposedException">The pool has been disposed.</exception>
    public bool TryGet(PageId pageId, out StoragePageHandle? handle)
    {
        lock (_syncRoot)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            if (_entries.TryGetValue((long)pageId, out var entry))
            {
                entry.PinCount++;
                Touch(entry);
                AssertFrameLocked((long)pageId, entry);
                handle = new StoragePageHandle(pageId, entry, this);
                return true;
            }

            handle = null;
            return false;
        }
    }

    /// <summary>
    /// Forces eviction of a specific page from the pool. The page must not be pinned.
    /// If the page is dirty, it is flushed to disk before eviction.
    /// </summary>
    /// <param name="pageId">The identifier of the page to evict.</param>
    /// <param name="stream">The storage stream to flush to if the page is dirty.</param>
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
            AssertInvariantsLocked();
        }
    }

    /// <summary>
    /// Flushes all dirty pages in the buffer pool to the backing storage stream.
    /// </summary>
    /// <param name="stream">The storage stream to write dirty pages to.</param>
    public void FlushAll(StorageStream stream)
    {
        lock (_syncRoot)
        {
            WriteGuard?.Invoke();

            foreach (var kvp in _entries)
            {
                if (kvp.Value.IsDirty)
                {
                    WriteBack(stream, (PageId)kvp.Key, kvp.Value);
                }
            }

            stream.Flush();
            AssertInvariantsLocked();
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
            if (_entries.TryGetValue((long)pageId, out var entry))
            {
                if (entry.IsDirty)
                {
                    WriteBack(stream, pageId, entry);
                }

                AssertFrameLocked((long)pageId, entry);
            }
        }
    }

    /// <summary>
    /// Reads a page's bytes from the stream as they stand, whether or not the page is resident,
    /// without verifying or caching them: the debug consistency check's view of what the last
    /// write-back left (#1253).
    /// </summary>
    /// <remarks>
    /// Under the pool lock, like every page read and write-back of the pool, so the read never
    /// overlaps a write-back or an extension of the stream.
    /// </remarks>
    /// <param name="pageId">The page to read.</param>
    /// <param name="stream">The stream to read from.</param>
    /// <param name="destination">A <see cref="Page.Size"/>-byte buffer.</param>
    /// <returns>False when the page lies past the end of the stream, which holds nothing of it.</returns>
    internal bool TryReadStored(PageId pageId, StorageStream stream, byte[] destination)
    {
        lock (_syncRoot)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            if (((long)pageId + 1) * Page.Size > stream.Length)
            {
                return false;
            }

            stream.ReadPage(pageId, destination);
            return true;
        }
    }

    /// <summary>
    /// Grows the stream to at least <paramref name="requiredLength"/> bytes; never shrinks it.
    /// </summary>
    /// <remarks>
    /// Runs under the pool lock, which every page read and write-back of the pool also
    /// holds, so the length check and the growth are one step with respect to page I/O: an
    /// extension can neither cut off a page another allocation just covered nor replace the
    /// stream's storage under a write-back (an in-memory stream copies its array when it
    /// grows, and a write into the old array would be lost while the page is recorded clean).
    /// </remarks>
    /// <param name="stream">The stream to extend.</param>
    /// <param name="requiredLength">The minimum length the stream must have.</param>
    internal void EnsureLength(StorageStream stream, long requiredLength)
    {
        lock (_syncRoot)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            if (stream.Length < requiredLength)
            {
                WriteGuard?.Invoke();
                stream.SetLength(requiredLength);
            }
        }
    }

    /// <summary>
    /// Writes back up to <paramref name="maxPages"/> dirty pages without evicting
    /// them — the paced write-back pass a page-writer worker performs between
    /// checkpoints. Each write-back honors the write-ahead gate like any other.
    /// </summary>
    /// <remarks>
    /// Pinned pages are skipped: page content changes only under a pin, so an unpinned
    /// page is quiescent and its image on the stream is the page as its last writer left
    /// it. A pinned dirty page waits for a later pass, an eviction, or the checkpoint.
    /// </remarks>
    /// <param name="stream">The stream to write to.</param>
    /// <param name="maxPages">The maximum number of dirty pages to write.</param>
    /// <returns>The number of pages written.</returns>
    internal int FlushSome(StorageStream stream, int maxPages)
    {
        lock (_syncRoot)
        {
            int written = 0;

            foreach (var kvp in _entries)
            {
                if (written >= maxPages)
                {
                    break;
                }

                if (kvp.Value.PinCount == 0 && kvp.Value.IsDirty)
                {
                    WriteBack(stream, (PageId)kvp.Key, kvp.Value);
                    written++;
                }
            }

            AssertInvariantsLocked();
            return written;
        }
    }

    /// <summary>
    /// Checks the pool's structural invariants and throws
    /// <see cref="InvalidOperationException"/> naming the first violation: every resident
    /// entry has a non-negative pin count, is not on the recycle stack, and owns exactly
    /// one node of the access list keyed by its page; the access list holds nothing else;
    /// every recycled entry is unpinned, detached, and not resident.
    /// </summary>
    /// <remarks>
    /// Compiled into every configuration, so tests can run it in the Release configuration
    /// CI uses. Debug builds additionally run it after every operation that changes the
    /// pool's structure — a pin miss, an eviction, a removal, a flush — and check only the
    /// frame involved after a pin hit or an unpin (#1240); release builds run it only when
    /// called.
    /// </remarks>
    internal void CheckInvariants()
    {
        lock (_syncRoot)
        {
            CheckInvariantsLocked();
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        lock (_syncRoot)
        {
            // No buffer is released here: buffers are pinned-heap arrays that stay valid
            // while any handle still references one, so a handle outliving its pool can
            // never write into reclaimed memory.
            _disposed = true;
            _entries.Clear();
            _accessOrder.Clear();
            _recycled.Clear();
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

    private void UnpinLocked(BufferEntry entry)
    {
        if (entry.PinCount > 0)
        {
            entry.PinCount--;
        }
        else
        {
            // Release builds ignore an over-release: the count stays at zero.
            FailOverReleaseLocked(entry);
        }

        // An unpin changes one pin count: check that frame only.
        if (entry.Node is not null)
        {
            AssertFrameLocked(entry.Node.Value, entry);
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
                // A foreground write: the thread that needed the frame (a pin, or a shrink) pays
                // for the write-back, not a page writer.
                long pageLsn = WriteBack(stream, (PageId)node.Value, entry);
                StorageEventSource.Log.CountForegroundPageWrite();
                StorageEventSource.Log.DirtyPageEvicted(this, (PageId)node.Value, pageLsn);
            }

            RemoveLocked(node.Value, entry);
            return;
        }

        StorageEventSource.Log.BufferPoolExhausted(this, _capacity);
        throw new StorageIOException("Buffer pool is full and all pages are pinned.");
    }

    /// <summary>
    /// Takes a recycled entry when one is available, otherwise allocates a fresh buffer
    /// on the pinned object heap.
    /// </summary>
    private BufferEntry TakeEntryLocked()
    {
        if (_recycled.Count > 0)
        {
            var recycledEntry = _recycled.Pop();
            recycledEntry.IsRecycled = false;
            recycledEntry.PinCount = 0;
            recycledEntry.MarkClean(Volatile.Read(ref recycledEntry.Version));
            recycledEntry.Node = null;
            return recycledEntry;
        }

        return new BufferEntry(GC.AllocateArray<byte>(Page.Size, pinned: true));
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
        entry.PinCount = 0;
        entry.MarkClean(Volatile.Read(ref entry.Version));
        entry.Node = null;
        entry.IsRecycled = true;
        _recycled.Push(entry);
    }

    /// <summary>
    /// Refuses a loaded page whose header describes bytes past the end of its buffer.
    /// </summary>
    /// <remarks>
    /// <see cref="Page.AsSpan"/> and <see cref="Page.AsBodySpan"/> size their spans from
    /// the header's overflow flag and size, and every pool buffer holds exactly
    /// <see cref="Page.Size"/> bytes. Nothing writes overflow pages yet, and a page whose
    /// stored checksum is zero is not verified, so without this check a damaged or crafted
    /// header would hand the B-tree and every other span user a writable span past the
    /// buffer — the out-of-bounds write #1157 was about, reached through the header.
    /// </remarks>
    /// <param name="page">The page just read into a pool buffer.</param>
    /// <param name="pageId">The page identifier, for the exception.</param>
    /// <exception cref="StorageCorruptionException">The header's overflow area does not fit the buffer.</exception>
    private static void VerifyFitsBuffer(Page page, PageId pageId)
    {
        if (page.IsOverflow && (page.OverflowSize < 0 || page.OverflowSize > Page.Size - Page.HeaderSize))
        {
            throw new StorageCorruptionException(
                pageId,
                $"Page {(long)pageId} declares an overflow area of {page.OverflowSize} bytes, which does not fit its {Page.Size}-byte buffer.");
        }
    }

    /// <summary>
    /// Writes a page's current image to the stream under a fresh checksum. Enforces the
    /// write-ahead rule first: the journal must be durable up to the LSN of the image
    /// being written before that image may reach the data stream.
    /// </summary>
    /// <remarks>
    /// The page is copied before anything else, because a writer holding a pin may change
    /// it at any moment: the checksum is computed over the copy and the copy is what is
    /// written, so the stream never receives bytes that disagree with their checksum. The
    /// write-ahead LSN is read from the copy too — a writer stamps a page's LSN before it
    /// changes the bytes that record covers, so the copy's LSN covers every change in it.
    /// The entry is recorded clean only up to the version observed before the copy.
    /// </remarks>
    /// <returns>The LSN the written image carries (for the eviction's event).</returns>
    private long WriteBack(StorageStream stream, PageId pageId, BufferEntry entry)
    {
        // Before anything else: an offline storage writes no page, whatever its LSN.
        WriteGuard?.Invoke();

        long version = Volatile.Read(ref entry.Version);
        new ReadOnlySpan<byte>(entry.Page.Pointer, Page.Size).CopyTo(_writeBackImage);

        long pageLsn;
        fixed (byte* image = _writeBackImage)
        {
            pageLsn = new Page(image).Lsn;
        }

        WriteBackAudit?.Invoke((long)pageId, pageLsn);

        if (pageLsn > 0)
        {
            WriteAheadGate?.Invoke(pageLsn);
        }

        PageChecksum.Stamp(_writeBackImage);
        stream.WritePage(pageId, _writeBackImage);
        StorageEventSource.Log.CountPageWrite();
        entry.MarkClean(version);
        return pageLsn;
    }

    // The per-operation checks below are debug-only; the structural check they share
    // (CheckInvariantsLocked) is compiled into every configuration.
    //
    // The full walk is O(resident pages), so it runs only where the pool's structure
    // changes: a pin miss (which may evict and recycle), an explicit eviction, a flush. A pin
    // hit or an unpin changes one entry's pin count and LRU position, and checks just that
    // frame. Running the walk on every access made Debug runs of the 100,000-row cascade
    // test spend about 77% of their time in it (#1240), 99.7% of it on hits and unpins.

    [Conditional("DEBUG")]
    private void AssertInvariantsLocked() => CheckInvariantsLocked();

    /// <summary>
    /// Checks the one frame a pin hit or an unpin changed: resident under its page, owning
    /// its node of the access list, not recycled, and with a non-negative pin count.
    /// </summary>
    [Conditional("DEBUG")]
    private void AssertFrameLocked(long id, BufferEntry entry)
    {
        if (entry.PinCount < 0)
        {
            FailInvariantLocked($"page {id} has pin count {entry.PinCount}");
        }

        if (entry.IsRecycled)
        {
            FailInvariantLocked($"page {id} is resident but its entry is marked recycled");
        }

        if (!_entries.TryGetValue(id, out var resident) || !ReferenceEquals(resident, entry))
        {
            FailInvariantLocked($"page {id} is not resident under its entry");
        }

        if (entry.Node is null || !ReferenceEquals(entry.Node.List, _accessOrder) || entry.Node.Value != id)
        {
            FailInvariantLocked($"page {id} does not own a node of the access list keyed by its id");
        }
    }

    [Conditional("DEBUG")]
    private void AssertResidentLocked(BufferEntry entry)
    {
        if (entry.IsRecycled || entry.Node is null || !_entries.TryGetValue(entry.Node.Value, out var resident) || !ReferenceEquals(resident, entry))
        {
            FailInvariantLocked("a handle released a pin on an entry that is no longer resident (recycled while pinned)");
        }
    }

    [Conditional("DEBUG")]
    private static void FailOverReleaseLocked(BufferEntry entry)
    {
        FailInvariantLocked($"page {entry.Node?.Value} was unpinned more times than it was pinned");
    }

    private static void FailInvariantLocked(string violation)
    {
        throw new InvalidOperationException($"Buffer pool invariant violated: {violation}.");
    }

    private void CheckInvariantsLocked()
    {
        if (_entries.Count > _capacity)
        {
            FailInvariantLocked($"{_entries.Count} resident pages exceed the capacity of {_capacity}");
        }

        if (_accessOrder.Count != _entries.Count)
        {
            FailInvariantLocked($"the access list holds {_accessOrder.Count} nodes for {_entries.Count} resident pages");
        }

        var resident = _invariantScratch;
        resident.Clear();

        try
        {
            foreach (var (id, entry) in _entries)
            {
                if (entry.PinCount < 0)
                {
                    FailInvariantLocked($"page {id} has pin count {entry.PinCount}");
                }

                if (entry.IsRecycled)
                {
                    FailInvariantLocked($"page {id} is resident but its entry is marked recycled");
                }

                if (!resident.Add(entry))
                {
                    FailInvariantLocked($"page {id} shares its entry with another resident page");
                }

                if (entry.Node is null || !ReferenceEquals(entry.Node.List, _accessOrder) || entry.Node.Value != id)
                {
                    FailInvariantLocked($"page {id} does not own a node of the access list keyed by its id");
                }
            }

            for (var node = _accessOrder.First; node is not null; node = node.Next)
            {
                if (!_entries.TryGetValue(node.Value, out var entry) || !ReferenceEquals(entry.Node, node))
                {
                    FailInvariantLocked($"the access list holds a node for page {node.Value} that no resident entry owns");
                }
            }

            foreach (var entry in _recycled)
            {
                if (!entry.IsRecycled || entry.PinCount != 0 || entry.Node is not null)
                {
                    FailInvariantLocked("a recycled entry is still pinned, attached to the access list, or not marked recycled");
                }

                if (resident.Contains(entry))
                {
                    FailInvariantLocked("a recycled entry is still resident");
                }
            }
        }
        finally
        {
            resident.Clear();
        }
    }

    /// <summary>
    /// Holds a page buffer and the associated page metadata.
    /// </summary>
    internal sealed unsafe class BufferEntry
    {
        public readonly byte[] Buffer;
        public readonly Page Page;
        public int PinCount;
        public bool IsRecycled;
        public LinkedListNode<long>? Node;

        // Advanced by every MarkDirty (any thread, no lock); the entry is dirty while it
        // is ahead of the version the last write-back copied.
        public long Version;
        private long _cleanVersion;

        public BufferEntry(byte[] buffer)
        {
            // A pinned-object-heap array never moves, so its address is stable for the
            // array's whole lifetime; the entry (and every handle on it) keeps it alive.
            Buffer = buffer;
            Page = new Page((byte*)Marshal.UnsafeAddrOfPinnedArrayElement(buffer, 0));
        }

        public bool IsDirty => Volatile.Read(ref Version) != Volatile.Read(ref _cleanVersion);

        public void MarkDirty() => Interlocked.Increment(ref Version);

        public void MarkClean(long version) => Volatile.Write(ref _cleanVersion, version);
    }
}
