using System.Collections.Generic;
using System.Threading;

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

    internal void Forget(ulong predecessor)
    {
        lock (_sync) { _links.Remove(predecessor); }
    }

    internal void Add(ulong predecessor, ulong successor, TransactionSequence writer)
    {
        lock (_sync)
        {
            var link = new Link(successor, writer);
            _links[predecessor] = link;
            _retired.Enqueue((predecessor, link));
        }
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
