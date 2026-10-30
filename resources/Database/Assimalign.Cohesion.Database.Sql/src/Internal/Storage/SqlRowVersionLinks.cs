using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Database.Storage;
using Assimalign.Cohesion.Database.Transactions;

namespace Assimalign.Cohesion.Database.Sql.Internal;

/// <summary>
/// Open-storage row lineage. Only readers whose snapshots predate a replacement need its
/// link; no transaction or statement survives reopen, so this is not a persisted contract.
/// A link is usable only when its writer equals the locked predecessor's deleter stamp.
/// </summary>
[global::System.Diagnostics.DebuggerDisplay("SqlRowVersionLinks")]
[global::System.Diagnostics.DebuggerTypeProxy(typeof(SqlRowVersionLinks.DebugView))]
internal sealed class SqlRowVersionLinks
{
    private readonly object _sync = new();
    private readonly Dictionary<ulong, Link> _links = [];
    private readonly Queue<(ulong Predecessor, Link Link)> _retired = [];

    private Dictionary<ulong, UndoScope>? _scopes;

    internal void Forget(ulong predecessor, TransactionSequence writer = default)
    {
        lock (_sync)
        {
            if (_scopes?.TryGetValue(writer.Value, out var scope) == true && scope.Active) { RecordUndo(predecessor, writer, scope); }
            _links.Remove(predecessor);
        }
    }

    internal void Add(ulong predecessor, ulong successor, TransactionSequence writer)
    {
        lock (_sync)
        {
            UndoScope? scope = null;
            if (_scopes?.TryGetValue(writer.Value, out scope) == true && scope.Active) { RecordUndo(predecessor, writer, scope); }
            var link = new Link(successor, writer);
            _links[predecessor] = link;
            if (scope?.Active == true) { scope.Retired.Add((predecessor, link)); }
            else { _retired.Enqueue((predecessor, link)); }
        }
    }

    internal void SetUndoScope(TransactionSequence writer, bool active, RecordSpaceVersionStore ledger)
    {
        lock (_sync)
        {
            if (active)
            {
                _scopes ??= new();
                if (!_scopes.TryGetValue(writer.Value, out var scope)) { _scopes.Add(writer.Value, scope = new UndoScope(ledger)); }
                scope.Active = true;
            }
            else if (_scopes?.TryGetValue(writer.Value, out var scope) == true) { scope.Active = false; }
        }
    }

    internal void EndUndoScope(TransactionContext context)
    {
        lock (_sync)
        {
            if (_scopes is null || !_scopes.Remove(context.Sequence.Value, out var scope)) { return; }
            if (context.State == TransactionState.Committed)
            { foreach (var entry in scope.Retired) { _retired.Enqueue(entry); } }
        }
    }

    private void RecordUndo(ulong predecessor, TransactionSequence writer, UndoScope scope)
    {
        bool existed = _links.TryGetValue(predecessor, out var before);
        int retired = scope.Retired.Count;
        var undo = new LinkUndo(() =>
        {
            lock (_sync)
            {
                if (existed) { _links[predecessor] = before; } else { _links.Remove(predecessor); }
                if (scope.Retired.Count > retired) { scope.Retired.RemoveRange(retired, scope.Retired.Count - retired); }
            }
        });
        scope.Ledger.RecordIndexEntryCreated(writer, undo, ReadOnlyMemory<byte>.Empty, 0);
    }

    internal int RetainedCount
    {
        get { lock (_sync) { int count = _retired.Count; if (_scopes is not null) { foreach (var scope in _scopes.Values) { count += scope.Retired.Count; } } return count; } }
    }

    [System.Diagnostics.DebuggerDisplay("UndoScope")]
    [System.Diagnostics.DebuggerTypeProxy(typeof(UndoScope.DebugView))]
    private sealed class UndoScope
    {
        internal UndoScope(RecordSpaceVersionStore ledger) { Ledger = ledger; }
        internal RecordSpaceVersionStore Ledger { get; }
        internal bool Active { get; set; }
        internal List<(ulong Predecessor, Link Link)> Retired { get; } = new();
        private sealed class DebugView { public DebugView(UndoScope value) { _ = value; } }
    }

    [System.Diagnostics.DebuggerDisplay("LinkUndo")]
    [System.Diagnostics.DebuggerTypeProxy(typeof(LinkUndo.DebugView))]
    private sealed class LinkUndo : RecordVersionIndex
    {
        private readonly Action _restore;
        internal LinkUndo(Action restore) { _restore = restore; }
        protected override ValueTask EraseCoreAsync(StorageTransaction transaction, ReadOnlyMemory<byte> key, ulong entryReference, TransactionSequence writer, CancellationToken cancellationToken)
        { _restore(); return default; }
        protected override ValueTask ClearDeleterCoreAsync(StorageTransaction transaction, ReadOnlyMemory<byte> key, ulong entryReference, TransactionSequence writer, CancellationToken cancellationToken) => default;
        protected override void OnStatementRolledBackCore() => _restore();
        private sealed class DebugView { public DebugView(LinkUndo value) { _ = value; } }
    }

    internal bool TryGet(ulong predecessor, TransactionSequence deleter, out ulong successor)
    {
        lock (_sync)
        {
            if (_links.TryGetValue(predecessor, out var link) && link.Writer == deleter)
            {
                successor = link.Successor;
                return true;
            }
        }
        successor = 0;
        return false;
    }

    internal void Prune(TransactionSequence bound, CancellationToken cancellationToken = default)
    {
        // Drain all eligible work, but never monopolize the monitor after a long snapshot.
        // Releasing between batches also lets a writer publish its link without keeping
        // the coordinator apply gate behind the entire maintenance backlog.
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int removed = 0;
            lock (_sync)
            {
                // Publication need not follow writer order. A newer head may retain older
                // links conservatively until the horizon passes it, never remove one early.
                while (removed < 1024 && _retired.TryPeek(out var entry) && entry.Link.Writer < bound)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    _retired.Dequeue();
                    if (_links.TryGetValue(entry.Predecessor, out var current) && current == entry.Link)
                    { _links.Remove(entry.Predecessor); }
                    removed++;
                }
                if (removed < 1024)
                {
                    // Hysteresis avoids repeatedly copying growing/steady-state storage.
                    // Trim under the same monitor as publication and lookup.
                    if (_links.EnsureCapacity(0) > 1024 && _links.Count < _links.EnsureCapacity(0) / 4)
                    { _links.TrimExcess(); }
                    if (_retired.EnsureCapacity(0) > 1024 && _retired.Count < _retired.EnsureCapacity(0) / 4)
                    { _retired.TrimExcess(); }
                }
            }
            if (removed < 1024) { return; }
        }
    }

    internal int Count { get { lock (_sync) { return _links.Count; } } }

    private readonly record struct Link(ulong Successor, TransactionSequence Writer);

    private sealed class DebugView
    {
        public DebugView(SqlRowVersionLinks value) { _ = value; }
    }
}
