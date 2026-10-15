using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

using Assimalign.Cohesion.Database.Sql.Catalog.Internal;
using Assimalign.Cohesion.Database.Storage;
using Assimalign.Cohesion.Database.Transactions;
using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Sql.Internal;

/// <summary>Retires unreachable owner chains in durable, bounded storage brackets.</summary>
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
                    if (!CanReclaim(pending)) { continue; }
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
                        token.ThrowIfCancellationRequested();
                        bracket.Commit();
                    }
                    pending.Pages += reclaimed;
                    Interlocked.Add(ref _reclaimedPages, reclaimed);
                    AfterBatch?.Invoke(pending.ObjectId, reclaimed);

                    if (reclaimed < BatchSize)
                    {
                        // Data durability precedes intent removal. A crash in between just
                        // visits the remaining owner directory again; freed pages have no owner.
                        token.ThrowIfCancellationRequested();
                        using var completion = _database.CatalogStorage.BeginTransaction();
                        _database.CatalogStorage.DeleteRow(completion, pending.PageId, pending.SlotIndex);
                        completion.Commit();
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
        using var iterator = _database.CatalogStorage.GetUnitIterator();
        while (iterator.MoveNext())
        {
            cancellationToken.ThrowIfCancellationRequested();
            var unit = iterator.Current;
            var reader = new DatabaseKeyReader(unit.Data.Span);
            if (reader.ReadInt32() != SqlReclamationRecord.Kind) { continue; }
            var intent = SqlReclamationRecord.Decode(ref reader);
            if (_pending.ContainsKey(intent.ObjectId)) { continue; }

            // Reading raw storage may see the intent while its catalog bracket is
            // still applying. The locked catalog lookup waits for that publication.
            if (_database.Catalog.TryGetTable(intent.Schema, intent.Name, out var table)
                && table.ObjectId == intent.ObjectId)
            {
                Volatile.Write(ref _discover, 1);
                continue;
            }

            // Allocate the fence AFTER observing the committed unpublication, never
            // use the DROP transaction's earlier begin sequence. Every possible old
            // reader is at or below it, including snapshots begun during the DROP.
            var fence = _database.Coordinator.BeginAsync(IsolationLevel.Snapshot, cancellationToken)
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
            _pending.Add(intent.ObjectId, new Pending(intent.ObjectId, unit.PageId, unit.SlotIndex, fence.Sequence, epoch));
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

    private sealed class Pending
    {
        internal Pending(ulong objectId, PageId pageId, int slotIndex, TransactionSequence fence, long epoch)
        {
            ObjectId = objectId;
            PageId = pageId;
            SlotIndex = slotIndex;
            Fence = fence;
            Epoch = epoch;
        }
        internal ulong ObjectId { get; }
        internal PageId PageId { get; }
        internal int SlotIndex { get; }
        internal TransactionSequence Fence { get; }
        internal long Epoch { get; }
        internal bool Started { get; set; }
        internal long Pages { get; set; }
    }

}
