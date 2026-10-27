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
                ObjectDisposedException.ThrowIf(_disposed, this);
                return EntryBytes * (long)(_entries.Count + (_parent ? 1 : 0));
            }
        }
    }

    internal async ValueTask AcquireAsync(GraphDatabase database, GraphOperation operation,
        LockResource resource, CancellationToken token = default, Func<ValueTask>? beforePatternGrant = null)
    {
        var manager = database.Coordinator.LockManager;
        var owner = operation.Context.Sequence;
        var parent = LockResource.Object(GraphLockNamespaces.MergePatterns);
        bool acquireParent;
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
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
            operation.EnsureActive();
        }
        bool escalate;
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
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

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed) { return; }
            _disposed = true;
            _entries.Clear();
        }
    }

    private sealed class DebugView
    {
        public DebugView(GraphMergeLocks value) { _ = value; }
    }
}
