using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Database.Storage.Internal;

using Assimalign.Cohesion.Database.Storage.Units;

/// <summary>
/// A bounded clock cache over pinned-heap page buffers. Cached pins own only frame-local
/// state; mapping changes and admission use the pool monitor, never while doing page I/O.
/// </summary>
[global::System.Diagnostics.DebuggerDisplay("StorageBufferPool")]
[global::System.Diagnostics.DebuggerTypeProxy(typeof(global::Assimalign.Cohesion.Database.Storage.Internal.StorageBufferPool.DebugView))]
internal sealed unsafe class StorageBufferPool : IDisposable
{
    private readonly ConcurrentDictionary<long, BufferEntry> _entries = new();
    private readonly LinkedList<long> _accessOrder = new(); // clock hand = First
    private readonly Stack<BufferEntry> _recycled = new();
    private LinkedListNode<long>? _clockHand;
    private readonly Dictionary<long, LoadOperation> _loads = new();
    private readonly object _syncRoot = new();
    // Flushes serialize with maintenance. Cold reads/evictions never take this gate.
    private readonly object _flushGate = new();
    private readonly object _extensionGate = new();
    private readonly object[] _pageIo = CreateIoGates();
    private readonly HashSet<BufferEntry> _invariantScratch = new(ReferenceEqualityComparer.Instance);
    private int _dirtyPageCount;
    private int _capacity;
    private int _loadingFrames;
    private int _activeIo;
    private bool _maintenance;
    private bool _disposed;
    private long _writeBackWaits;
    private const int flushBatchPages = 16;

    internal int DirtyPageCount => Volatile.Read(ref _dirtyPageCount);
    internal Action<long>? WriteAheadGate;
    internal Action? WriteGuard;
    internal Action<long, long>? WriteBackAudit;
    internal string StorageName { get; set; } = string.Empty;
    internal long WriteBackWaits => Interlocked.Read(ref _writeBackWaits);

    internal StorageBufferPool(int capacity)
    {
        ValidateCapacity(capacity);
        _capacity = capacity;
    }

    public int Capacity { get { lock (_syncRoot) { return _capacity; } } }
    public int Count { get { lock (_syncRoot) { return _entries.Count; } } }

    private static void ValidateCapacity(int capacity)
    {
        if (capacity < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(capacity), ErrorMessages.BufferPoolValidateCapacityFailure);
        }
    }

    private static object[] CreateIoGates()
    {
        var gates = new object[128];
        for (int i = 0; i < gates.Length; i++) { gates[i] = new object(); }
        return gates;
    }

    private object IoGate(long id) => _pageIo[(int)((ulong)id % (uint)_pageIo.Length)];

    public StoragePageHandle Pin(PageId pageId, StorageStream stream) => PinCore(pageId, stream, overwrite: false);
    internal StoragePageHandle PinForOverwrite(PageId pageId, StorageStream stream) => PinCore(pageId, stream, overwrite: true);

    private StoragePageHandle PinCore(PageId pageId, StorageStream stream, bool overwrite)
    {
        long id = (long)pageId;
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed), this);
        if (TryPin(id, out var hit)) { return new StoragePageHandle(pageId, hit!, this); }
        while (true)
        {
            LoadOperation? waiting = null;
            LoadOperation? loading = null;
            BufferEntry? frame = null;
            long? victim = null;
            lock (_syncRoot)
            {
                WaitForAdmissionLocked();
                if (TryPin(id, out hit)) { return new StoragePageHandle(pageId, hit!, this); }
                if (_loads.TryGetValue(id, out waiting))
                {
                    // One completion per page fault; a waiter's task is created lazily.
                    waiting.PrepareWait();
                    Interlocked.Increment(ref _writeBackWaits);
                }
                else if (_entries.TryGetValue(id, out _))
                {
                    WaitForIoLocked(); // this page is reserved for eviction
                    continue;
                }
                else
                {
                    if (_entries.Count + _loadingFrames < _capacity)
                    {
                        frame = TakeEntryLocked();
                        _loadingFrames++;
                    }
                    else
                    {
                        frame = ReserveVictimLocked();
                        if (frame is null) { WaitForIoLocked(); continue; }
                        victim = frame.ResidentId;
                    }
                    loading = new LoadOperation();
                    _loads.Add(id, loading);
                    _activeIo++;
                }
            }
            if (waiting is not null) { waiting.Wait(); continue; }

            bool ownsLoadingFrame = victim is null;
            bool published = false;
            try
            {
                if (victim is long oldId)
                {
                    WriteVictim(stream, oldId, frame!);
                    lock (_syncRoot)
                    {
                        DetachLocked(oldId, frame!);
                        _loadingFrames++;
                        ownsLoadingFrame = true;
                        Monitor.PulseAll(_syncRoot);
                    }
                }
                LoadPage(stream, pageId, frame!, overwrite);
                lock (_syncRoot)
                {
                    frame!.ResidentId = id;
                    frame.Referenced = 1;
                    frame.WriteInProgress = false;
                    frame.ClockNode.Value = id;
                    frame.Node = frame.ClockNode;
                    _accessOrder.AddLast(frame.Node);
                    _entries[id] = frame;
                    frame.PublishPin();
                    _loadingFrames--;
                    ownsLoadingFrame = false;
                    CompleteLoadLocked(id, loading!, null);
                    published = true;
                    AssertInvariantsLocked();
                }
                return new StoragePageHandle(pageId, frame!, this);
            }
            catch (Exception failure)
            {
                if (published) { throw; }
                lock (_syncRoot)
                {
                    if (ownsLoadingFrame) { _loadingFrames--; RecycleLocked(frame!); }
                    else if (victim is not null)
                    {
                        frame!.WriteInProgress = false;
                        frame.AllowPins(); // failed write leaves the old dirty mapping intact
                    }
                    CompleteLoadLocked(id, loading!, failure);
                    AssertInvariantsLocked();
                }
                throw;
            }
        }
    }

    private bool TryPin(long id, out BufferEntry? entry)
    {
        if (_entries.TryGetValue(id, out entry) && TryPinEntry(id, entry))
        {
            return true;
        }
        entry = null;
        return false;
    }

    private bool TryPinEntry(long id, BufferEntry entry)
    {
        if (entry.TryPin())
        {
            // A lookup may have raced a complete eviction and reuse of this same object.
            if (Volatile.Read(ref entry.ResidentId) == id)
            {
                Volatile.Write(ref entry.Referenced, 1);
                AssertFrameLocked(id, entry);
                return true;
            }
            entry.ReleasePin();
        }
        return false;
    }

    private void LoadPage(StorageStream stream, PageId pageId, BufferEntry frame, bool overwrite)
    {
        lock (IoGate((long)pageId))
        {
            if (!overwrite && (long)pageId * Page.Size < stream.Length)
            {
                bool verifyingChecksum = false;
                try
                {
                    stream.ReadPage(pageId, frame.Buffer);
                    StorageEventSource.Log.CountPageRead();
                    verifyingChecksum = true;
                    PageChecksum.Verify(frame.Buffer, pageId);
                    verifyingChecksum = false;
                    _ = frame.Page.GetValidatedBodySize(pageId);
                }
                catch
                {
                    if (verifyingChecksum) { StorageEventSource.Log.PageChecksumFailed(this, pageId); }
                    throw;
                }
            }
            else { Array.Clear(frame.Buffer); }
        }
    }

    private void CompleteLoadLocked(long id, LoadOperation operation, Exception? failure)
    {
        _loads.Remove(id);
        _activeIo--;
        operation.Complete(failure);
        Monitor.PulseAll(_syncRoot);
    }

    public bool TryGet(PageId pageId, out StoragePageHandle? handle)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed), this);
        if (TryPin((long)pageId, out var entry))
        {
            handle = new StoragePageHandle(pageId, entry!, this);
            return true;
        }
        handle = null;
        return false;
    }

    public void Unpin(PageId pageId)
    {
        if (_entries.TryGetValue((long)pageId, out var entry)) { Unpin(entry); }
    }

    internal void Unpin(BufferEntry entry)
    {
        if (Volatile.Read(ref _disposed)) { return; }
        AssertResidentLocked(entry);
        if (!entry.ReleasePin()) { FailOverReleaseLocked(entry); }
    }

    // Two bounded revolutions: clear a reference on the first encounter, then claim
    // an unpinned frame atomically. Hits never move the shared clock hand.
    private BufferEntry? ReserveVictimLocked()
    {
        bool writing = _loadingFrames > 0;
        int count = _accessOrder.Count;
        for (int step = 0; step < count * 2; step++)
        {
            var node = _clockHand ?? _accessOrder.First!;
            _clockHand = node.Next ?? _accessOrder.First;
            var entry = _entries[node.Value];
            if (entry.WriteInProgress) { writing = true; continue; }
            if (entry.PinCount != 0) { continue; }
            if (Interlocked.Exchange(ref entry.Referenced, 0) != 0 && step < count) { continue; }
            if (!entry.TryReserve()) { continue; }
            entry.WriteInProgress = true;
            return entry;
        }
        if (writing) { return null; }
        StorageEventSource.Log.BufferPoolExhausted(this, _capacity);
        throw new StorageIOException(ErrorMessages.PoolReserveVictimLockedStorageIO);
    }

    private void WaitForIoLocked()
    {
        Interlocked.Increment(ref _writeBackWaits);
        Monitor.Wait(_syncRoot);
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    private void WaitForAdmissionLocked()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        while (_maintenance)
        {
            Monitor.Wait(_syncRoot);
            ObjectDisposedException.ThrowIf(_disposed, this);
        }
    }

    public void Evict(PageId pageId, StorageStream stream)
    {
        long id = (long)pageId;
        BufferEntry entry;
        lock (_syncRoot)
        {
            while (true)
            {
                WaitForAdmissionLocked();
                if (_loads.ContainsKey(id)) { WaitForIoLocked(); continue; }
                if (!_entries.TryGetValue(id, out entry!)) { return; }
                if (entry.WriteInProgress) { WaitForIoLocked(); continue; }
                if (!entry.TryReserve()) { throw new StorageIOException(string.Format(System.Globalization.CultureInfo.CurrentCulture, ErrorMessages.StorageBufferPoolEvictStorageIO, id)); }
                entry.WriteInProgress = true;
                _activeIo++;
                break;
            }
        }
        bool removed = false;
        try
        {
            WriteVictim(stream, id, entry);
            lock (_syncRoot) { DetachLocked(id, entry); RecycleLocked(entry); removed = true; }
        }
        finally
        {
            lock (_syncRoot)
            {
                if (!removed) { entry.WriteInProgress = false; entry.AllowPins(); }
                _activeIo--;
                Monitor.PulseAll(_syncRoot);
                AssertInvariantsLocked();
            }
        }
    }

    internal void Resize(int capacity, StorageStream stream)
    {
        ValidateCapacity(capacity);
        int previous;
        lock (_flushGate)
        {
            var victims = new List<BufferEntry>();
            lock (_syncRoot)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                _maintenance = true;
                while (_activeIo != 0) { Monitor.Wait(_syncRoot); }
                previous = _capacity;
                int pinned = 0;
                foreach (var entry in _entries.Values) { if (entry.PinCount > 0) { pinned++; } }
                if (pinned > capacity)
                {
                    _maintenance = false;
                    Monitor.PulseAll(_syncRoot);
                    throw new StorageIOException(string.Format(System.Globalization.CultureInfo.CurrentCulture, ErrorMessages.StorageBufferPoolResizeStorageIO, capacity, pinned));
                }
                try
                {
                    int needed = Math.Max(0, _entries.Count - capacity);
                    // Reserve the complete shrink before I/O: later cached pins cannot
                    // turn a previously valid shrink into an over-capacity state.
                    while (victims.Count < needed)
                    {
                        var victim = ReserveVictimLocked();
                        if (victim is not null) { victims.Add(victim); }
                        else { throw new StorageIOException(string.Format(System.Globalization.CultureInfo.CurrentCulture, ErrorMessages.StorageBufferPoolResizeStorageIO, capacity, _entries.Count - victims.Count)); }
                    }
                }
                catch
                {
                    foreach (var entry in victims) { entry.WriteInProgress = false; entry.AllowPins(); }
                    _maintenance = false;
                    Monitor.PulseAll(_syncRoot);
                    throw;
                }
            }
            try
            {
                foreach (var entry in victims)
                {
                    long id = entry.ResidentId;
                    WriteVictim(stream, id, entry);
                    lock (_syncRoot) { DetachLocked(id, entry); RecycleLocked(entry); }
                }
                lock (_syncRoot) { _capacity = capacity; }
            }
            catch
            {
                lock (_syncRoot) { _capacity = Math.Max(capacity, _entries.Count); }
                throw;
            }
            finally
            {
                lock (_syncRoot)
                {
                    foreach (var entry in victims)
                    {
                        if (!entry.IsRecycled) { entry.WriteInProgress = false; entry.AllowPins(); }
                    }
                    while (_recycled.Count > _capacity - _entries.Count) { _recycled.Pop(); }
                    _maintenance = false;
                    Monitor.PulseAll(_syncRoot);
                    AssertInvariantsLocked();
                }
            }
        }
        if (previous != capacity) { StorageEventSource.Log.BufferPoolResized(this, previous, capacity); }
    }

    private void WriteVictim(StorageStream stream, long id, BufferEntry entry)
    {
        if (!entry.IsDirty) { return; }
        byte[] image = ArrayPool<byte>.Shared.Rent(Page.Size);
        try
        {
            lock (IoGate(id))
            {
                WriteGuard?.Invoke();
                long version = Volatile.Read(ref entry.Version);
                entry.Buffer.AsSpan().CopyTo(image);
                long lsn = WriteImage(stream, (PageId)id, entry, image, version);
                StorageEventSource.Log.CountForegroundPageWrite();
                StorageEventSource.Log.DirtyPageEvicted(this, (PageId)id, lsn);
            }
        }
        finally { ArrayPool<byte>.Shared.Return(image); }
    }

    public void FlushAll(StorageStream stream)
    {
        lock (_flushGate)
        {
            FlushSnapshot(stream, SnapshotDirty(int.MaxValue, false), false);
            stream.Flush();
        }
    }

    private List<KeyValuePair<long, BufferEntry>>? SnapshotDirty(int maximum, bool skipPinned)
    {
        WriteGuard?.Invoke();
        lock (_syncRoot)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (DirtyPageCount == 0) { return null; }
            List<KeyValuePair<long, BufferEntry>>? dirty = null;
            foreach (var pair in _entries)
            {
                if (pair.Value.IsDirty && (!skipPinned || pair.Value.PinCount == 0))
                {
                    (dirty ??= new List<KeyValuePair<long, BufferEntry>>(Math.Min(_entries.Count, maximum))).Add(pair);
                    if (dirty.Count == maximum) { break; }
                }
            }
            return dirty;
        }
    }

    private int FlushSnapshot(StorageStream stream, List<KeyValuePair<long, BufferEntry>>? dirty, bool skipPinned)
    {
        if (dirty is null) { return 0; }
        byte[] image = ArrayPool<byte>.Shared.Rent(Page.Size);
        int written = 0;
        try
        {
            for (int index = 0; index < dirty.Count; index++)
            {
                var pair = dirty[index];
                if (FlushEntry(stream, pair.Key, pair.Value, image, skipPinned)) { written++; }
                if ((index + 1) % flushBatchPages == 0 && index + 1 < dirty.Count) { Thread.Yield(); }
            }
        }
        finally { ArrayPool<byte>.Shared.Return(image); }
        lock (_syncRoot) { AssertInvariantsLocked(); }
        return written;
    }

    private bool FlushEntry(StorageStream stream, long id, BufferEntry expected, byte[] image, bool skipPinned)
    {
        long version;
        lock (_syncRoot)
        {
            while (true)
            {
                if (!_entries.TryGetValue(id, out var entry) || !ReferenceEquals(entry, expected) ||
                    !entry.IsDirty || (skipPinned && entry.PinCount != 0)) { return false; }
                if (entry.WriteInProgress) { WaitForIoLocked(); continue; }
                version = Volatile.Read(ref entry.Version);
                entry.Buffer.AsSpan().CopyTo(image);
                entry.WriteInProgress = true;
                break;
            }
        }
        try
        {
            lock (IoGate(id)) { WriteImage(stream, (PageId)id, expected, image, version); }
            return true;
        }
        finally
        {
            lock (_syncRoot)
            {
                expected.WriteInProgress = false;
                Monitor.PulseAll(_syncRoot);
                AssertFrameLocked(id, expected);
            }
        }
    }

    internal void FlushPage(PageId pageId, StorageStream stream)
    {
        lock (_flushGate)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed), this);
            if (_entries.TryGetValue((long)pageId, out var entry))
            {
                byte[] image = ArrayPool<byte>.Shared.Rent(Page.Size);
                try { FlushEntry(stream, (long)pageId, entry, image, false); }
                finally { ArrayPool<byte>.Shared.Return(image); }
            }
        }
    }

    internal int FlushSome(StorageStream stream, int maxPages)
    {
        if (maxPages <= 0) { return 0; }
        lock (_flushGate) { return FlushSnapshot(stream, SnapshotDirty(maxPages, true), true); }
    }

    internal bool TryReadStored(PageId pageId, StorageStream stream, byte[] destination)
    {
        object gate = IoGate((long)pageId);
        // The write-back audit reads its own previous physical image reentrantly.
        // That read belongs to its parent's admission, even during maintenance.
        bool nested = Monitor.IsEntered(gate);
        if (!nested) { BeginIo(); }
        try
        {
            lock (gate)
            {
                if (((long)pageId + 1) * Page.Size > stream.Length) { return false; }
                stream.ReadPage(pageId, destination);
                return true;
            }
        }
        finally { if (!nested) { EndIo(); } }
    }

    internal void EnsureLength(StorageStream stream, long requiredLength)
    {
        BeginIo();
        try
        {
            lock (_extensionGate)
            {
                if (stream.Length < requiredLength) { WriteGuard?.Invoke(); stream.SetLength(requiredLength); }
            }
        }
        finally { EndIo(); }
    }

    private void BeginIo() { lock (_syncRoot) { WaitForAdmissionLocked(); _activeIo++; } }
    private void EndIo() { lock (_syncRoot) { _activeIo--; Monitor.PulseAll(_syncRoot); } }

    internal void CheckInvariants() { lock (_syncRoot) { CheckInvariantsLocked(); } }

    public void Dispose()
    {
        lock (_flushGate)
        {
            lock (_syncRoot)
            {
                _maintenance = true;
                while (_activeIo != 0) { Monitor.Wait(_syncRoot); }
                Volatile.Write(ref _disposed, true);
                _entries.Clear();
                _accessOrder.Clear();
                _clockHand = null;
                _recycled.Clear();
                _maintenance = false;
                Monitor.PulseAll(_syncRoot);
            }
        }
    }

    private BufferEntry TakeEntryLocked()
    {
        BufferEntry entry = _recycled.Count == 0
            ? new BufferEntry(GC.AllocateArray<byte>(Page.Size, pinned: true), this)
            : _recycled.Pop();
        entry.IsRecycled = false;
        entry.WriteInProgress = true;
        entry.MarkClean(Volatile.Read(ref entry.Version));
        entry.ReserveNew();
        return entry;
    }

    private void DetachLocked(long id, BufferEntry entry)
    {
        if (entry.Node is not null)
        {
            if (ReferenceEquals(_clockHand, entry.Node))
            {
                _clockHand = entry.Node.Next ?? _accessOrder.First;
                if (ReferenceEquals(_clockHand, entry.Node)) { _clockHand = null; }
            }
            _accessOrder.Remove(entry.Node);
            entry.Node = null;
        }
        _entries.TryRemove(id, out _);
    }

    private void RecycleLocked(BufferEntry entry)
    {
        entry.MarkClean(Volatile.Read(ref entry.Version));
        entry.WriteInProgress = false;
        entry.IsRecycled = true;
        entry.Node = null;
        entry.ReserveNew();
        _recycled.Push(entry);
    }

    [global::System.Diagnostics.DebuggerDisplay("LoadOperation")]
    [global::System.Diagnostics.DebuggerTypeProxy(typeof(global::Assimalign.Cohesion.Database.Storage.Internal.StorageBufferPool.LoadOperation.DebugView))]
    private sealed class LoadOperation
    {
        private TaskCompletionSource? _completion;
        private ExceptionDispatchInfo? _failure;
        internal void PrepareWait() => _completion ??= new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal void Wait() { _completion!.Task.GetAwaiter().GetResult(); _failure?.Throw(); }
        internal void Complete(Exception? failure)
        {
            if (failure is not null) { _failure = ExceptionDispatchInfo.Capture(failure); }
            _completion?.SetResult();
        }

        private sealed class DebugView
        {
            public DebugView(global::Assimalign.Cohesion.Database.Storage.Internal.StorageBufferPool.LoadOperation value)
            {
                _ = value;
            }
        }
    }

    private long WriteImage(StorageStream stream, PageId pageId, BufferEntry entry, byte[] imageBytes, long version)
    {
        WriteGuard?.Invoke();

        long pageLsn;
        fixed (byte* image = imageBytes)
        {
            pageLsn = new Page(image).Lsn;
        }

        WriteBackAudit?.Invoke((long)pageId, pageLsn);

        if (pageLsn > 0)
        {
            WriteAheadGate?.Invoke(pageLsn);
        }

        PageChecksum.Stamp(imageBytes.AsSpan(0, Page.Size));
        stream.WritePage(pageId, imageBytes);
        StorageEventSource.Log.CountPageWrite();
        entry.MarkClean(version);
        return pageLsn;
    }

    // The per-operation checks below are debug-only; the structural check they share
    // (CheckInvariantsLocked) is compiled into every configuration.
    //
    // The full walk is O(resident pages), so it runs only where the pool's structure
    // changes: a pin miss (which may evict and recycle), an explicit eviction, a flush. A pin
    // hit or an unpin changes frame-local pin/reference state, and checks just that
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
        lock (_syncRoot)
        {
            if (Volatile.Read(ref _disposed)) { return; }
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
    }

    [Conditional("DEBUG")]
    private void AssertResidentLocked(BufferEntry entry)
    {
        lock (_syncRoot)
        {
            if (Volatile.Read(ref _disposed)) { return; }
            if (entry.IsRecycled || entry.Node is null || !_entries.TryGetValue(entry.Node.Value, out var resident) || !ReferenceEquals(resident, entry))
            {
                FailInvariantLocked("a handle released a pin on an entry that is no longer resident (recycled while pinned)");
            }
        }
    }

    [Conditional("DEBUG")]
    private static void FailOverReleaseLocked(BufferEntry entry)
    {
        FailInvariantLocked($"page {entry.Node?.Value} was unpinned more times than it was pinned");
    }

    private static void FailInvariantLocked(string violation)
    {
        throw new InvalidOperationException(string.Format(System.Globalization.CultureInfo.CurrentCulture, ErrorMessages.BufferPoolFailInvariantLockedFailure, violation));
    }

    private void CheckInvariantsLocked()
    {
        if (_entries.Count + _loadingFrames > _capacity)
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
                if (!entry.IsRecycled || entry.PinCount != 0 || entry.WriteInProgress || entry.Node is not null)
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
    [global::System.Diagnostics.DebuggerDisplay("BufferEntry")]
    [global::System.Diagnostics.DebuggerTypeProxy(typeof(global::Assimalign.Cohesion.Database.Storage.Internal.StorageBufferPool.BufferEntry.DebugView))]
    internal sealed unsafe class BufferEntry
    {
        public readonly byte[] Buffer;
        public readonly Page Page;
        private const long unavailable = 1L << 32;
        private long _pins = unavailable;
        public int PinCount
        {
            get => unchecked((int)Volatile.Read(ref _pins));
            set => Interlocked.Exchange(ref _pins, (Volatile.Read(ref _pins) & unavailable) | (uint)value);
        }
        internal long ResidentId;
        internal int Referenced;
        internal bool TryPin()
        {
            long state = Volatile.Read(ref _pins);
            while ((state & unavailable) == 0)
            {
                long observed = Interlocked.CompareExchange(ref _pins, state + 1, state);
                if (observed == state) { return true; }
                state = observed;
            }
            return false;
        }
        internal bool ReleasePin()
        {
            long state = Volatile.Read(ref _pins);
            while (unchecked((int)state) > 0)
            {
                long observed = Interlocked.CompareExchange(ref _pins, state - 1, state);
                if (observed == state) { return true; }
                state = observed;
            }
            return false;
        }
        internal bool TryReserve() => Interlocked.CompareExchange(ref _pins, unavailable, 0) == 0;
        internal void ReserveNew() => Volatile.Write(ref _pins, unavailable);
        internal void PublishPin() => Volatile.Write(ref _pins, 1);
        internal void AllowPins() => Volatile.Write(ref _pins, 0);
        public bool IsRecycled;
        // Protected by the pool monitor. A flush or eviction owns frame I/O until
        // its finally releases the lease; pins may still read or dirty its live bytes.
        public bool WriteInProgress;
        internal readonly LinkedListNode<long> ClockNode = new(0);
        public LinkedListNode<long>? Node;

        // Advanced by every MarkDirty (any thread, no lock); the entry is dirty while it
        // is ahead of the version the last write-back copied.
        public long Version;
        private long _cleanVersion;
        private readonly object _dirtySync = new();
        private readonly StorageBufferPool _pool;

        // Even when stable, odd while a mutable handle or rollback can change the
        // live bytes. Private to this frame: no process-wide work counter.
        private long _restoreSequence;
        private object? _writer;
        private int _writerThread;
        private int _writeDepth;
        // Only the owning writer reads this while updating a record-space hint.
        internal bool HasNestedWriter => _writeDepth > 1;
        private int _writeEpoch;
        private int _restoring;
        private int _restoreEpoch;
        // Allocated only if a bracket completes while a mutable handle is still
        // pinned. Its eventual disposal must not release a later writer's epoch.
        private Dictionary<int, int>? _retiredEpochs;

        // Test rendezvous when an unlatched reader must wait for this frame's writer.
        internal Action? ReadWaitStarted { get; set; }
        internal Action? ReadCopied { get; set; }
        internal Action? OwnReadAccepted { get; set; }

        public BufferEntry(byte[] buffer, StorageBufferPool pool)
        {
            // A pinned-object-heap array never moves, so its address is stable for the
            // array's whole lifetime; the entry (and every handle on it) keeps it alive.
            _pool = pool;
            Buffer = buffer;
            Page = new Page((byte*)Marshal.UnsafeAddrOfPinnedArrayElement(buffer, 0));
        }

        public bool IsDirty => Volatile.Read(ref Version) != Volatile.Read(ref _cleanVersion);

        public void MarkDirty()
        {
            // Pool -> entry is the only lock order. Dirtying never enters the pool monitor.
            lock (_dirtySync)
            {
                if (!IsDirty) { Interlocked.Increment(ref _pool._dirtyPageCount); }
                Interlocked.Increment(ref Version);
            }
        }

        public void MarkClean(long version)
        {
            lock (_dirtySync)
            {
                bool dirty = IsDirty;
                Volatile.Write(ref _cleanVersion, version);
                if (dirty && !IsDirty) { Interlocked.Decrement(ref _pool._dirtyPageCount); }
            }
        }

        /// <summary>
        /// Starts a private-copy read, confirmed with IsUnchangedSince. Both forward
        /// writes and rollback use PostgreSQL's change-count protocol
        /// (src/include/utils/backend_status.h:101-116,181-238); no code copied.
        /// </summary>
        public long BeginRead()
        {
            long sequence = Volatile.Read(ref _restoreSequence);
            return (sequence & 1) == 0 ? sequence : WaitForWriter();
        }

        /// <summary>
        /// Returns whether no write boundary ran since <paramref name="sequence"/> was read
        /// (<see cref="BeginRead"/>): every load the read made before this call happened before the
        /// sequence is read again, so a read this confirms saw one state of the page.
        /// </summary>
        public bool IsUnchangedSince(long sequence)
        {
            ReadCopied?.Invoke();
            Volatile.ReadBarrier();
            return Volatile.Read(ref _restoreSequence) == sequence;
        }

        internal int BeginWrite(object owner) => BeginWriteCore(owner, restoring: false);

        private int BeginWriteCore(object owner, bool restoring)
        {
            lock (_dirtySync)
            {
                RetireCompletedWriter();
                while (_writer is not null && !ReferenceEquals(_writer, owner))
                {
                    Monitor.Wait(_dirtySync);
                    RetireCompletedWriter();
                }
                // Exclude own-thread reads before publishing the restore boundary.
                if (restoring) { Volatile.Write(ref _restoring, 1); }
                if (_writer is null)
                {
                    do { _writeEpoch = unchecked(_writeEpoch + 1); }
                    while (_writeEpoch == 0 || (_retiredEpochs?.ContainsKey(_writeEpoch) ?? false));
                    _writer = owner;
                    _writerThread = Environment.CurrentManagedThreadId;
                    Interlocked.Increment(ref _restoreSequence);
                }
                else
                {
                    // Keep it odd, but invalidate an own-thread read across a
                    // nested write as well as a read across the outer scope.
                    Interlocked.Add(ref _restoreSequence, 2);
                }
                _writeDepth++;
                return _writeEpoch;
            }
        }

        internal void AdoptWriter(int epoch, object previousOwner, object owner)
        {
            lock (_dirtySync)
            {
                if (_writeEpoch == epoch && ReferenceEquals(_writer, previousOwner)) { _writer = owner; }
            }
        }

        internal void EndWrite(int epoch)
        {
            lock (_dirtySync)
            {
                if (_writer is not null && epoch == _writeEpoch)
                {
                    if (--_writeDepth == 0) { FinishWriter(); }
                    else { Interlocked.Add(ref _restoreSequence, 2); }
                }
                else if (_retiredEpochs is not null && _retiredEpochs.TryGetValue(epoch, out int count))
                {
                    if (count == 1) { _retiredEpochs.Remove(epoch); }
                    else { _retiredEpochs[epoch] = count - 1; }
                }
            }
        }

        public void BeginRestore(object? owner = null)
        {
            _restoreEpoch = BeginWriteCore(owner ?? this, restoring: true);
        }

        public void EndRestore()
        {
            lock (_dirtySync)
            {
                Volatile.Write(ref _restoring, 0);
                EndWrite(_restoreEpoch);
            }
        }

        private void RetireCompletedWriter()
        {
            if (_writer is StorageTransaction transaction && !transaction.IsActive)
            {
                (_retiredEpochs ??= new())[_writeEpoch] = _writeDepth;
                FinishWriter();
            }
        }

        private void FinishWriter()
        {
            _writeDepth = 0;
            _writer = null;
            Volatile.Write(ref _writerThread, 0);
            Interlocked.Increment(ref _restoreSequence);
            Monitor.PulseAll(_dirtySync);
        }

        private long WaitForWriter()
        {
            var spinner = new SpinWait();
            bool observed = false;
            while (true)
            {
                // Capture before permission: a restore starting after the check
                // must invalidate this copy, never supply it with a newer token.
                long sequence = Volatile.Read(ref _restoreSequence);
                if ((sequence & 1) == 0) { return sequence; }
                if (Volatile.Read(ref _writerThread) == Environment.CurrentManagedThreadId
                    && Volatile.Read(ref _restoring) == 0)
                {
                    OwnReadAccepted?.Invoke();
                    return sequence;
                }
                if (!observed)
                {
                    ReadWaitStarted?.Invoke();
                    observed = true;
                }
                lock (_dirtySync) { RetireCompletedWriter(); }
                spinner.SpinOnce();
            }
        }

        private sealed class DebugView
        {
            private readonly global::Assimalign.Cohesion.Database.Storage.Internal.StorageBufferPool.BufferEntry _target;

            public DebugView(global::Assimalign.Cohesion.Database.Storage.Internal.StorageBufferPool.BufferEntry value)
            {
                _target = value;
            }

            public int PinCount => _target.PinCount;
            public bool IsRecycled => _target.IsRecycled;
            public bool WriteInProgress => _target.WriteInProgress;
            public long Version => _target.Version;
            public bool IsDirty => _target.IsDirty;
        }
    }

    private sealed class DebugView
    {
        public DebugView(global::Assimalign.Cohesion.Database.Storage.Internal.StorageBufferPool value)
        {
            _ = value;
        }
    }
}
