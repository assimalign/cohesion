using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Database.Transactions;

namespace Assimalign.Cohesion.Database.Graph.Internal;

/// <summary>Bounded, transaction-owned pattern bookkeeping; allocated only by MERGE.</summary>
[System.Diagnostics.DebuggerDisplay("GraphMergeLocks")]
[System.Diagnostics.DebuggerTypeProxy(typeof(GraphMergeLocks.DebugView))]
internal sealed class GraphMergeLocks : IDisposable
{
    internal const int EntryLimit = 128;
    internal const int EntryBytes = 640;
    private readonly object _sync = new();
    private readonly HashSet<LockResource> _entries = [];
    private bool _parent;
    private bool _escalated;
    private bool _disposed;

    internal long RetainedBytes
    {
        get
        {
            lock (_sync)
            {
                if (_disposed) { throw GraphOperation.Ended(); }
                return EntryBytes * (long)(_entries.Count + (_parent ? 1 : 0));
            }
        }
    }

    internal ValueTask AcquireAsync(GraphDatabase database, GraphOperation operation,
        LockResource resource, CancellationToken token = default, Func<ValueTask>? beforePatternGrant = null,
        Func<ValueTask>? beforeParentCheck = null)
        // Dispatch before constructing either async state machine. The ordinary path retains
        // its existing locals/fields even when a contended lock request suspends it.
        => database.Store.ScopeLedger(operation.Context.Sequence) is { } ledger
            ? AcquireMarkedAsync(database, operation, resource, ledger, token, beforePatternGrant, beforeParentCheck)
            : AcquireUnmarkedAsync(database, operation, resource, token, beforePatternGrant, beforeParentCheck);

    private async ValueTask AcquireUnmarkedAsync(GraphDatabase database, GraphOperation operation,
        LockResource resource, CancellationToken token, Func<ValueTask>? beforePatternGrant,
        Func<ValueTask>? beforeParentCheck)
    {
        var owner = operation.Context.Sequence;
        var manager = database.Coordinator.LockManager;
        var parent = LockResource.Object(GraphLockNamespaces.MergePatterns);
        bool acquireParent;
        lock (_sync)
        {
            if (_disposed) { throw GraphOperation.Ended(); }
            acquireParent = !_parent;
            if (acquireParent)
            {
                // Parent entry, tracker and bounded collection bookkeeping.
                Charge();
                _parent = true;
            }
        }
        if (acquireParent)
        {
            await manager.AcquireAsync(owner, parent, LockMode.IntentExclusive, token).ConfigureAwait(false);
            if (beforeParentCheck is not null) { await beforeParentCheck().ConfigureAwait(false); }
            operation.EnsureMergeActive();
        }
        bool escalate;
        lock (_sync)
        {
            if (_disposed) { throw GraphOperation.Ended(); }
            if (_escalated || _entries.Contains(resource)) { return; }
            escalate = _entries.Count == EntryLimit;
            if (!escalate)
            {
                // Includes the manager's entry/grant/owner-set and our entry/lease storage.
                // Charge before acquisition, including requests which may wait.
                Charge();
                _entries.Add(resource);
            }
        }
        if (escalate)
        {
            await manager.AcquireAsync(owner, parent, LockMode.Exclusive, token).ConfigureAwait(false);
            lock (_sync) { _escalated = true; }
            return;
        }
        // Instance-free test seam: end after bookkeeping but before the manager sees the
        // request, exercising an actual late grant to an owner already released by teardown.
        if (beforePatternGrant is not null) { await beforePatternGrant().ConfigureAwait(false); }
        await manager.AcquireAsync(owner, resource, LockMode.Exclusive, token).ConfigureAwait(false);

        void Charge() => operation.Merge.ChargeLock();
    }

    private async ValueTask AcquireMarkedAsync(GraphDatabase database, GraphOperation operation, LockResource resource,
        RecordSpaceVersionStore ledger, CancellationToken token, Func<ValueTask>? beforePatternGrant, Func<ValueTask>? beforeParentCheck)
    {
        var manager = database.Coordinator.LockManager;
        var owner = operation.Context.Sequence;
        var parent = LockResource.Object(GraphLockNamespaces.MergePatterns);
        if (await ChangeTrackedAsync(database, operation, resource, ledger, TrackedChange.Parent, token).ConfigureAwait(false) != 0)
        {
            await manager.AcquireAsync(owner, parent, LockMode.IntentExclusive, token).ConfigureAwait(false);
            if (beforeParentCheck is not null) { await beforeParentCheck().ConfigureAwait(false); }
            operation.EnsureMergeActive();
        }
        int change = await ChangeTrackedAsync(database, operation, resource, ledger, TrackedChange.Entry, token).ConfigureAwait(false);
        if (change == 0) { return; }
        if (change == 2)
        {
            await manager.AcquireAsync(owner, parent, LockMode.Exclusive, token).ConfigureAwait(false);
            await ChangeTrackedAsync(database, operation, resource, ledger, TrackedChange.Escalation, token).ConfigureAwait(false);
            return;
        }
        if (beforePatternGrant is not null) { await beforePatternGrant().ConfigureAwait(false); }
        await manager.AcquireAsync(owner, resource, LockMode.Exclusive, token).ConfigureAwait(false);
    }

    // A marked tracker journals through ordinary apply admission, so an end drains it before
    // removing the writer's ledger. Lock order: manager/context admission (released) -> apply
    // gate -> tracker -> ledger monitor. No tracker monitor spans admission, a gate wait,
    // lock-table request or await. Undo releases the ledger monitor before Restore takes ours.
    // The existing immutable scope directory keeps the unmarked path free of new monitors,
    // allocations and fields; repeated marked holdings also need no empty apply bracket.
    private ValueTask<int> ChangeTrackedAsync(GraphDatabase database, GraphOperation operation, LockResource resource,
        RecordSpaceVersionStore ledger, TrackedChange change, CancellationToken token)
    {
        lock (_sync)
        {
            if (_disposed) { throw GraphOperation.Ended(); }
            if (change == TrackedChange.Parent && _parent || change == TrackedChange.Escalation && _escalated ||
                change == TrackedChange.Entry && (_escalated || _entries.Contains(resource))) { return ValueTask.FromResult(0); }
            if (change == TrackedChange.Entry && _entries.Count == EntryLimit) { return ValueTask.FromResult(2); }
        }
        return ApplyTrackedChangeAsync(database, operation, resource, ledger, change, token);
    }

    private ValueTask<int> ApplyTrackedChangeAsync(GraphDatabase database, GraphOperation operation, LockResource resource,
        RecordSpaceVersionStore ledger, TrackedChange change, CancellationToken token)
        => database.Coordinator.ApplyStatementAsync(operation.Context, _ =>
        {
            lock (_sync)
            {
                if (_disposed) { throw GraphOperation.Ended(); }
                if (change == TrackedChange.Parent && _parent || change == TrackedChange.Escalation && _escalated ||
                    change == TrackedChange.Entry && (_escalated || _entries.Contains(resource))) { return 0; }
                if (change == TrackedChange.Entry && _entries.Count == EntryLimit) { return 2; }
                if (change != TrackedChange.Escalation) { operation.Merge.ChargeLock(); }
                ledger.RecordIndexEntryCreated(operation.Context.Sequence,
                    new LockUndo(this, change == TrackedChange.Entry ? resource : null, _parent, _escalated), ReadOnlyMemory<byte>.Empty, 0);
                if (change == TrackedChange.Parent) { _parent = true; }
                else if (change == TrackedChange.Escalation) { _escalated = true; }
                else { _entries.Add(resource); }
                return 1;
            }
        }, token);

    private enum TrackedChange
    {
        Parent,
        Entry,
        Escalation,
    }

    private void Restore(LockResource? entry, bool parent, bool escalated)
    {
        lock (_sync)
        {
            if (_disposed) { return; }
            if (entry is { } resource) { _entries.Remove(resource); }
            _parent = parent;
            _escalated = escalated;
        }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed) { return; }
            _disposed = true;
            _entries.Clear();
        }
    }

    [System.Diagnostics.DebuggerDisplay("LockUndo")]
    [System.Diagnostics.DebuggerTypeProxy(typeof(LockUndo.DebugView))]
    private sealed class LockUndo : RecordVersionIndex
    {
        private readonly GraphMergeLocks _locks;
        private readonly LockResource? _entry;
        private readonly bool _parent;
        private readonly bool _escalated;

        internal LockUndo(GraphMergeLocks locks, LockResource? entry, bool parent, bool escalated)
        { _locks = locks; _entry = entry; _parent = parent; _escalated = escalated; }

        protected override ValueTask EraseCoreAsync(Assimalign.Cohesion.Database.Storage.StorageTransaction transaction,
            ReadOnlyMemory<byte> key, ulong entryReference, TransactionSequence writer, CancellationToken cancellationToken)
        { _locks.Restore(_entry, _parent, _escalated); return default; }
        protected override ValueTask ClearDeleterCoreAsync(Assimalign.Cohesion.Database.Storage.StorageTransaction transaction,
            ReadOnlyMemory<byte> key, ulong entryReference, TransactionSequence writer, CancellationToken cancellationToken) => default;
        protected override void OnStatementRolledBackCore() => _locks.Restore(_entry, _parent, _escalated);

        private sealed class DebugView { public DebugView(LockUndo value) { _ = value; } }
    }

    private sealed class DebugView
    {
        public DebugView(GraphMergeLocks value) { _ = value; }
    }
}
