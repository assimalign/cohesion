using System;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Database.Transactions;

namespace Assimalign.Cohesion.Database.Graph.Internal;

/// <summary>MERGE-only statement caches; the database writer protects both captured views.</summary>
[System.Diagnostics.DebuggerDisplay("GraphMergeStatement")]
[System.Diagnostics.DebuggerTypeProxy(typeof(GraphMergeStatement.DebugView))]
internal sealed class GraphMergeStatement : IDisposable
{
    private readonly GraphDatabase _database;
    private readonly GraphOperation _operation;
    private readonly bool _ownsLocks;
    private TransactionSnapshot? _latest;
    private bool _refreshed;
    private object? _identityKey;
    private GraphMergePatternIdentity? _identity;
    private GraphMemoryLease _memory;

    internal GraphMergeStatement(GraphDatabase database, GraphOperation operation, GraphMergeLocks? locks)
    {
        _database = database;
        _operation = operation;
        _ownsLocks = locks is null;
        Locks = locks ?? new();
        operation.QueryBudget ??= new(database.Engine.EngineOptions.QueryWorkBudget, database.Engine.EngineOptions.QueryMemoryBudget);
        _memory = operation.QueryBudget.Memory.Reserve(256);
    }

    internal GraphMergeLocks Locks { get; }
    internal TransactionSnapshot Latest => _latest ??= _database.LatestSnapshot(_operation.Context);
    internal bool SnapshotCaptured => _refreshed;

    internal async ValueTask RefreshAsync(bool waited, CancellationToken token = default)
    {
        if (_refreshed && !waited) { return; }
        _refreshed = false;
        await _operation.RefreshWriterSnapshotAsync(token).ConfigureAwait(false);
        _latest = null;
        _refreshed = true;
    }

    // A key is one immutable, fully evaluated pattern, including bound ids and equalities.
    // Reuse it across rows only when those inputs are unchanged; different evaluations use
    // different keys. The caller owns operand memory. Keep only the most recent key, so a
    // million distinct row keys cannot grow this cache. Never retain the factory closure.
    internal GraphMergePatternIdentity Identity(object evaluatedPattern, Func<GraphMergePatternIdentity> create)
    {
        ArgumentNullException.ThrowIfNull(evaluatedPattern);
        ArgumentNullException.ThrowIfNull(create);
        if (!ReferenceEquals(_identityKey, evaluatedPattern))
        {
            var identity = create();
            _identityKey = evaluatedPattern;
            _identity = identity;
        }
        return _identity!;
    }

    public void Dispose()
    {
        _identityKey = null;
        _identity = null;
        _latest = null;
        _memory.Dispose();
        // Also covers a tracker first published concurrently with the transaction's end.
        if (_ownsLocks || _operation.Context.State != TransactionState.Active) { Locks.Dispose(); }
    }

    private sealed class DebugView
    {
        public DebugView(GraphMergeStatement value) { _ = value; }
    }
}
