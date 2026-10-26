using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

using Assimalign.Cohesion.Database.Transactions;

using Assimalign.Cohesion.Database.Sql.Storage;

namespace Assimalign.Cohesion.Database.Sql.Internal;

/// <summary>Retires unreachable owner chains in durable, bounded storage brackets.</summary>
[global::System.Diagnostics.DebuggerDisplay("SqlTableReclamation")]
[global::System.Diagnostics.DebuggerTypeProxy(typeof(global::Assimalign.Cohesion.Database.Sql.Internal.SqlTableReclamation.DebugView))]
internal sealed class SqlTableReclamation
{
    internal const int BatchSize = 32;
    private readonly SqlDatabase _database;
    private readonly object _readersLock = new();
    private readonly object _runLock = new();
    private readonly Dictionary<long, int> _readers = new();
    private readonly Dictionary<ulong, Pending> _pending = new();
    private readonly CancellationTokenSource _stop = new();
    private long _epoch;
    private long _reclaimedPages;
    private int _discover = 1;
    private int _pendingCount;
    private int _running;
    private bool _closed;

    internal SqlTableReclamation(SqlDatabase database) { _database = database; }

    internal long ReclaimedPages => Interlocked.Read(ref _reclaimedPages);
    internal bool HasWork => Volatile.Read(ref _running) != 0 || Volatile.Read(ref _discover) != 0 || Volatile.Read(ref _pendingCount) != 0;
    internal Action<ulong, CancellationToken>? BeforeBatch { get; set; }
    internal Action<ulong, CancellationToken>? BeforeBatchCommit { get; set; }
    internal Action<ulong, int>? AfterBatch { get; set; }

    internal void Request()
    {
        Volatile.Write(ref _discover, 1);
        _database.Engine.UndoDeferredSignal.Set();
    }

    // A transaction can be rolled back by another flow while its statement still reads.
    // This lease outlives that rollback, from before planning through materialization.
    internal long EnterStatement()
    {
        lock (_readersLock)
        {
            _readers.TryGetValue(_epoch, out int count);
            _readers[_epoch] = count + 1;
            return _epoch;
        }
    }

    internal void ExitStatement(long epoch)
    {
        lock (_readersLock)
        {
            if (--_readers[epoch] == 0) { _readers.Remove(epoch); }
        }
    }

    internal void RunBatch(CancellationToken cancellationToken)
    {
        lock (_runLock)
        {
            if (_closed || !_database.Engine.ProvisioningBuildComplete) { return; }
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _stop.Token);
            CancellationToken token = linked.Token;
            token.ThrowIfCancellationRequested();
            Volatile.Write(ref _running, 1);
            try
            {
                if (Interlocked.Exchange(ref _discover, 0) != 0)
                {
                    try { Discover(token); }
                    catch { Volatile.Write(ref _discover, 1); throw; }
                }

                foreach (var pending in _pending.Values.ToArray())
                {
                    token.ThrowIfCancellationRequested();
                    // A prepared TRUNCATE owner can become live atomically with removal of
                    // its intent. Its statement/transaction pins prevent any free before that
                    // decision. Recheck after the pins clear, before touching owner pages.
                    if (!CanReclaim(pending)) { continue; }
                    if (!_database.Catalog.IsTableReclamationPending(pending.ObjectId))
                    {
                        _pending.Remove(pending.ObjectId);
                        Volatile.Write(ref _pendingCount, _pending.Count);
                        continue;
                    }
                    if (!pending.Started)
                    {
                        pending.Started = true;
                        SqlDatabaseEventSource.Log.TableReclamationStarted(_database, pending.ObjectId);
                    }
                    BeforeBatch?.Invoke(pending.ObjectId, token);
                    token.ThrowIfCancellationRequested();
                    int reclaimed;
                    // No coordinator apply gate or catalog/table lock covers the page walk.
                    // These identities can never acquire new writers or be reused by CREATE.
                    using (var bracket = _database.DataStorage.BeginTransaction())
                    {
                        reclaimed = _database.DataStorage.FreeOwnerPages(bracket, pending.ObjectId, BatchSize);
                        BeforeBatchCommit?.Invoke(pending.ObjectId, token);
                        token.ThrowIfCancellationRequested();
                        bracket.Commit();
                    }
                    pending.Pages += reclaimed;
                    Interlocked.Add(ref _reclaimedPages, reclaimed);
                    AfterBatch?.Invoke(pending.ObjectId, reclaimed);

                    // A short batch alone does not prove the directory is empty: FreeOwnerPages
                    // skips stale snapshot entries (#1273), so completion checks the directory.
                    if (reclaimed < BatchSize && !_database.DataStorage.HasOwnerPages(pending.ObjectId))
                    {
                        // Data durability precedes intent removal. A crash in between just
                        // visits the remaining owner directory again; freed pages have no owner.
                        token.ThrowIfCancellationRequested();
                        _database.Catalog.CompleteTableReclamationAsync(pending.ObjectId, token).GetAwaiter().GetResult();
                        _pending.Remove(pending.ObjectId);
                        Volatile.Write(ref _pendingCount, _pending.Count);
                        SqlDatabaseEventSource.Log.TableReclamationCompleted(_database, pending.ObjectId, pending.Pages);
                    }
                }
            }
            finally { Volatile.Write(ref _running, 0); }
        }
    }

    private void Discover(CancellationToken cancellationToken)
    {
        // The catalog copies committed identities under its publication lock. Discovery
        // never reads mutable slotted pages, and no catalog lock covers fencing or data I/O.
        foreach (ulong objectId in _database.Catalog.GetPendingTableReclamations())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_pending.ContainsKey(objectId)) { continue; }

            // Allocate the fence AFTER observing the committed unpublication, never
            // use the DROP transaction's earlier begin sequence. Every possible old
            // reader is at or below it, including snapshots begun during the DROP.
            var fence = _database.Coordinator.BeginSnapshotPinAsync(cancellationToken)
                .AsTask().GetAwaiter().GetResult();
            long epoch;
            try
            {
                lock (_readersLock) { epoch = _epoch++; }
            }
            finally
            {
                _database.Coordinator.RollbackAsync(fence, CancellationToken.None).AsTask().GetAwaiter().GetResult();
            }
            _pending.Add(objectId, new Pending(objectId, fence.Sequence, epoch));
            Volatile.Write(ref _pendingCount, _pending.Count);
        }
    }

    private bool CanReclaim(Pending pending)
    {
        if (_database.Coordinator.Manager.OldestActive <= pending.Fence
            || _database.Coordinator.GetOpenContexts().Any(context => context.Sequence <= pending.Fence))
        {
            return false;
        }
        lock (_readersLock) { return !_readers.Keys.Any(epoch => epoch <= pending.Epoch); }
    }

    internal void Stop()
    {
        _stop.Cancel();
        lock (_runLock) { _closed = true; }
    }

    [global::System.Diagnostics.DebuggerDisplay("Pending")]
    [global::System.Diagnostics.DebuggerTypeProxy(typeof(global::Assimalign.Cohesion.Database.Sql.Internal.SqlTableReclamation.Pending.DebugView))]
    private sealed class Pending
    {
        internal Pending(ulong objectId, TransactionSequence fence, long epoch)
        {
            ObjectId = objectId;
            Fence = fence;
            Epoch = epoch;
        }
        internal ulong ObjectId { get; }
        internal TransactionSequence Fence { get; }
        internal long Epoch { get; }
        internal bool Started { get; set; }
        internal long Pages { get; set; }

        private sealed class DebugView
        {
            public DebugView(global::Assimalign.Cohesion.Database.Sql.Internal.SqlTableReclamation.Pending value)
            {
                _ = value;
            }
        }
    }


    private sealed class DebugView
    {
        public DebugView(global::Assimalign.Cohesion.Database.Sql.Internal.SqlTableReclamation value)
        {
            _ = value;
        }
    }
}
