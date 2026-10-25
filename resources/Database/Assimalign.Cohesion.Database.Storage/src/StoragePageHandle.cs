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
/// A handle obtained with <see cref="Storage.OpenPageForWrite(StorageTransaction, PageId)"/>
/// is a mutable scope: dispose it promptly after the writes, and never mutate it after the
/// transaction completes. A handle obtained with <see cref="StoragePageManager.GetPage"/>
/// is a read pin; its native page view must not be used to bypass transactional writes.
/// Nested mutable handles for one page must stay on the same thread. A sole mutable
/// handle may move to a continuation thread; its scope also permits reads through
/// Storage there. Accessing Page from another execution context transfers that
/// permission with the handle and revokes it in the previous context.
/// Never use a mutable handle or its borrowed page memory
/// concurrently on multiple threads. Disposal may run on another thread.
///
/// <example>
/// <code>
/// using var handle = storage.OpenPageForWrite(transaction, pageId);
/// var page = handle.Page;
/// // Change the page inside the active transaction...
/// handle.MarkDirty(); // Signal that the page was modified
/// </code>
/// </example>
/// </remarks>
[global::System.Diagnostics.DebuggerDisplay("StoragePageHandle")]
[global::System.Diagnostics.DebuggerTypeProxy(typeof(global::Assimalign.Cohesion.Database.Storage.StoragePageHandle.DebugView))]
public sealed class StoragePageHandle : IDisposable
{
    private static readonly AsyncLocal<MutableScope?> _mutableScope = new(ScopeChanged);
    private readonly StorageBufferPool _pool;
    internal readonly StorageBufferPool.BufferEntry Entry;
    private int _disposed;
    private int _writeEpoch;

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
    /// <exception cref="StorageTransactionException">A mutable handle moved threads while another mutable scope for the page remains open.</exception>
    public Page Page
    {
        get
        {
#if DEBUG
            // After its pin is released the entry may already hold another page: a
            // reference through a released handle is a use-after-unpin.
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
#endif
            if (_writeEpoch != 0)
            {
                Entry.AccessWriter(_writeEpoch);
                if (Entry.HasMutableScopes && Entry.FindMutableScope(this) is { } owner) { EnterMutableScope(owner); }
            }
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

    // A writable handle owns an epoch until disposal. An allocating handle starts
    // with itself as owner, then adopts its physical bracket after first touch.
    internal void BeginWrite(object owner)
    {
        if (_writeEpoch == 0) { _writeEpoch = Entry.BeginWrite(owner); }
        else { Entry.AdoptWriter(_writeEpoch, this, owner); }
    }

    // Only public raw mutable handles need permission to flow through an await.
    // Typed synchronous record writes and ordinary pins allocate no ambient scope.
    internal void EnterMutableScope() => EnterMutableScope(Entry.RegisterMutableScope(this));

    private static void EnterMutableScope(MutableScopeOwner owner)
    {
        for (var scope = _mutableScope.Value; scope is not null; scope = scope.Previous)
        {
            if (ReferenceEquals(scope.Owner, owner) && scope.IsCurrent && scope.IsSoleContext) { return; }
        }
        // A disjoint context, or a fork still sharing the old node, takes a new
        // permission token. Revocation belongs to the handle, so the old context
        // cannot retain its bypass while the new owner exposes writable memory.
        var current = new MutableScope(owner, ActiveScope(_mutableScope.Value));
        owner.SetCurrent(current);
        _mutableScope.Value = current;
    }

    internal static bool OwnsReadScope(StorageBufferPool.BufferEntry entry, int epoch, bool writerThread)
    {
        var scope = _mutableScope.Value;
        while (scope is not null)
        {
            if (scope.IsCurrent && (scope.IsSoleContext || writerThread) && scope.TryGetHandle(out var handle)
                && ReferenceEquals(handle.Entry, entry) && handle._writeEpoch == epoch) { return true; }
            scope = scope.Previous;
        }
        return false;
    }

    private static MutableScope? ActiveScope(MutableScope? scope)
    {
        while (scope is not null && (!scope.IsCurrent || !scope.TryGetHandle(out _))) { scope = scope.Previous; }
        return scope;
    }

    private static void ScopeChanged(AsyncLocalValueChangedArgs<MutableScope?> change)
    {
        // A forked reader inherits ExecutionContext too. It must not become an own
        // reader while its writer's context is still running. An awaited continuation
        // is the sole active context and can read without binding to a physical thread.
        for (var scope = change.PreviousValue; scope is not null; scope = scope.Previous) { scope.ChangeContexts(-1); }
        for (var scope = change.CurrentValue; scope is not null; scope = scope.Previous) { scope.ChangeContexts(1); }
    }

    /// <summary>
    /// Releases the pin. Exactly one release happens per handle, even when two threads dispose
    /// it at once.
    /// </summary>
    public void Dispose()
    {
        // Exactly one release per handle, even when two threads dispose it at once.
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            if (_writeEpoch != 0) { Entry.EndWrite(_writeEpoch, this); }
            _pool.Unpin(Entry);
            var scope = _mutableScope.Value;
            var active = ActiveScope(scope);
            if (!ReferenceEquals(active, scope)) { _mutableScope.Value = active; }
        }
    }

    [global::System.Diagnostics.DebuggerDisplay("MutableScope")]
    [global::System.Diagnostics.DebuggerTypeProxy(typeof(MutableScope.DebugView))]
    internal sealed class MutableScope
    {
        private int _activeContexts;
        internal readonly MutableScopeOwner Owner;
        internal readonly MutableScope? Previous;
        internal bool IsSoleContext => Volatile.Read(ref _activeContexts) == 1;
        internal bool IsCurrent => Owner.IsCurrent(this);

        internal MutableScope(MutableScopeOwner owner, MutableScope? previous)
        {
            Owner = owner;
            Previous = previous;
        }

        // Cross-thread disposal can leave this context's old node in place. Keep
        // no strong reference to its handle/frame/pool while awaiting the next access.
        internal bool TryGetHandle(out StoragePageHandle handle)
            => Owner.TryGetHandle(out handle);

        internal void ChangeContexts(int difference)
        {
            Interlocked.Add(ref _activeContexts, difference);
            // Invalidate a copy that crossed a change in permission. This callback
            // takes no monitor and cannot wait or throw during context transitions.
            if (TryGetHandle(out var handle)) { handle.Entry.InvalidateReadPermission(); }
        }

        private sealed class DebugView
        {
            public DebugView(MutableScope value) { _ = value; }
        }
    }

    // Registered on the pinned frame rather than adding a field to every read
    // handle. Ordinary pins and synchronous typed writes keep their allocation size.
    [global::System.Diagnostics.DebuggerDisplay("MutableScopeOwner")]
    [global::System.Diagnostics.DebuggerTypeProxy(typeof(MutableScopeOwner.DebugView))]
    internal sealed class MutableScopeOwner
    {
        private readonly WeakReference<StoragePageHandle> _handle;
        private MutableScope? _current;
        internal MutableScopeOwner? Next;

        internal MutableScopeOwner(StoragePageHandle handle, MutableScopeOwner? next)
        {
            _handle = new(handle);
            Next = next;
        }

        internal bool Matches(StoragePageHandle handle)
            => _handle.TryGetTarget(out var target) && ReferenceEquals(target, handle);

        internal bool TryGetHandle(out StoragePageHandle handle)
            => _handle.TryGetTarget(out handle!) && Volatile.Read(ref handle._disposed) == 0;

        internal bool IsEpoch(int epoch) => TryGetHandle(out var handle) && handle._writeEpoch == epoch;

        internal bool IsCurrent(MutableScope scope) => ReferenceEquals(Volatile.Read(ref _current), scope);

        internal void SetCurrent(MutableScope scope)
        {
            Volatile.Write(ref _current, scope);
            if (TryGetHandle(out var handle)) { handle.Entry.InvalidateReadPermission(); }
        }

        private sealed class DebugView
        {
            public DebugView(MutableScopeOwner value) { _ = value; }
        }
    }

    private sealed class DebugView
    {
        private readonly global::Assimalign.Cohesion.Database.Storage.StoragePageHandle _target;

        public DebugView(global::Assimalign.Cohesion.Database.Storage.StoragePageHandle value)
        {
            _target = value;
        }

        public global::Assimalign.Cohesion.Database.Storage.PageId Id => _target.Id;
        public bool IsDirty => _target.IsDirty;
        public int PinCount => _target.PinCount;
    }
}
