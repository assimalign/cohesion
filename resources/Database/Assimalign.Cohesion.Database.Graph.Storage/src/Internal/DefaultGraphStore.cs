using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Assimalign.Cohesion.Database.Indexing;
using Assimalign.Cohesion.Database.Storage;
using Assimalign.Cohesion.Database.Transactions;

namespace Assimalign.Cohesion.Database.Graph.Storage.Internal;

internal sealed partial class DefaultGraphStore : IGraphStore
{
    private readonly GraphStorage _storage;
    private readonly TransactionCoordinator _coordinator;
    private readonly object _sync = new();
    private readonly Dictionary<(byte Kind, ulong Id), Reference> _records = new();

    internal DefaultGraphStore(GraphStorage storage, TransactionCoordinator coordinator)
    {
        _storage = storage;
        _coordinator = coordinator;
        using var iterator = storage.GetUnitIterator(2);
        while (iterator.MoveNext())
        {
            var unit = iterator.Current;
            var record = GraphRecordCodec.Decode(unit.Data);
            var (writer, _) = RecordVersionStamp.ReadStamps(unit.Data.Span);
            _records.Add((record.Kind, record.Id), new Reference(unit.PageId, unit.SlotIndex, writer));
        }
        OpenIndexes();
    }

    public async ValueTask<StoredGraphNode> CreateNodeAsync(IReadOnlyList<string> labels,
        IReadOnlyDictionary<string, object?> properties, ITransactionContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(labels);
        ArgumentNullException.ThrowIfNull(properties);
        foreach (string label in labels) { ArgumentException.ThrowIfNullOrWhiteSpace(label); }
        await LockAsync(context, cancellationToken).ConfigureAwait(false);
        ulong id = (ulong)_storage.ReserveTransactionSequence();
        var proposed = new GraphRecord(1, id, new StoredGraphNode(id, labels.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray(), properties));
        byte[] bytes = GraphRecordCodec.Encode(proposed, context.Sequence);
        var record = GraphRecordCodec.Decode(bytes); // Own immutable copies, never retain caller dictionaries.
        var node = record.Node!.Value;
        var indexes = Definitions(Latest(context));
        var changes = indexes.Where(index => node.Labels.Contains(index.Label!, StringComparer.Ordinal)
            && node.Properties.ContainsKey(index.PropertyKey!))
            .Select(index => (index.Id, Key: Composite(GraphRecordCodec.Key(node.Properties[index.PropertyKey!]), node.Id))).ToArray();
        var inserted = await ApplyAsync(context, async bracket =>
        {
            var location = Insert(bracket, bytes, context);
            foreach (var change in changes)
            {
                await InsertIndexAsync(change.Id, change.Key, location, context, cancellationToken).ConfigureAwait(false);
            }
            SaveRegistrations(bracket);
            return location;
        }, cancellationToken).ConfigureAwait(false);
        Add(record, inserted, context.Sequence);
        return node;
    }

    public StoredGraphNode? FindNode(ulong id, TransactionSnapshot snapshot) => Find(1, id, snapshot)?.Record.Node;
    public StoredGraphRelationship? FindRelationship(ulong id, TransactionSnapshot snapshot) => Find(2, id, snapshot)?.Record.Relationship;

    public IReadOnlyList<StoredGraphNode> GetNodes(string? label, TransactionSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        return Ids(1).Select(id => FindNode(id, snapshot)).Where(node => node is { } found
            && (label is null || found.Labels.Contains(label, StringComparer.Ordinal))).Select(node => node!.Value).OrderBy(node => node.Id).ToArray();
    }

    public async ValueTask<StoredGraphRelationship> CreateRelationshipAsync(ulong sourceId, ulong targetId, string type,
        IReadOnlyDictionary<string, object?> properties, ITransactionContext context, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(type);
        ArgumentNullException.ThrowIfNull(properties);
        await LockAsync(context, cancellationToken).ConfigureAwait(false);
        EnsureUnchanged(1, sourceId, context);
        EnsureUnchanged(1, targetId, context);
        if (FindNode(sourceId, context.Snapshot) is null || FindNode(targetId, context.Snapshot) is null)
        {
            throw new InvalidOperationException("A relationship requires two visible existing endpoints.");
        }
        ulong id = (ulong)_storage.ReserveTransactionSequence();
        byte[] bytes = GraphRecordCodec.Encode(new GraphRecord(2, id,
            Relationship: new StoredGraphRelationship(id, sourceId, targetId, type, properties)), context.Sequence);
        var record = GraphRecordCodec.Decode(bytes);
        var inserted = await ApplyAsync(context, async bracket =>
        {
            await EnsureAdjacencyAsync(context, cancellationToken).ConfigureAwait(false);
            var location = Insert(bracket, bytes, context);
            await InsertIndexAsync(AdjacencyId, Composite(IndexKey.FromUInt64(sourceId), id), location, context, cancellationToken).ConfigureAwait(false);
            if (targetId != sourceId)
            {
                await InsertIndexAsync(AdjacencyId, Composite(IndexKey.FromUInt64(targetId), id), location, context, cancellationToken).ConfigureAwait(false);
            }
            SaveRegistrations(bracket);
            return location;
        }, cancellationToken).ConfigureAwait(false);
        Add(record, inserted, context.Sequence);
        return record.Relationship!.Value;
    }

    public async ValueTask<IReadOnlyList<StoredGraphRelationship>> GetIncidentAsync(ulong nodeId, TransactionSnapshot snapshot,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (!_indexes.TryGetIndex(AdjacencyId, TreeName, out var index)) { return []; }
        var key = IndexKey.FromUInt64(nodeId);
        var result = new List<StoredGraphRelationship>();
        await using var cursor = index.OpenCursor(snapshot, new IndexKeyRange(Composite(key, 0), Composite(key, ulong.MaxValue), true, true));
        while (await cursor.MoveNextAsync(cancellationToken).ConfigureAwait(false))
        {
            var found = ReadAt(cursor.CurrentEntryReference, snapshot);
            if (found?.Record.Relationship is { } relationship && (relationship.SourceId == nodeId || relationship.TargetId == nodeId)
                && cursor.CurrentKey.Equals(Composite(key, relationship.Id)))
            {
                result.Add(relationship);
            }
        }
        return result;
    }

    public async ValueTask DeleteNodeAsync(ulong id, bool detach, ITransactionContext context, CancellationToken cancellationToken = default)
    {
        await LockAsync(context, cancellationToken).ConfigureAwait(false);
        EnsureUnchanged(1, id, context);
        var node = Find(1, id, context.Snapshot);
        if (node is null) { return; }
        var incident = await GetIncidentAsync(id, Latest(context), cancellationToken).ConfigureAwait(false);
        foreach (var relationship in incident) { EnsureUnchanged(2, relationship.Id, context); }
        if (incident.Count != 0 && !detach) { throw new InvalidOperationException("Cannot delete a connected node without DETACH DELETE."); }
        var indexes = Definitions(Latest(context));
        await ApplyAsync(context, async bracket =>
        {
            foreach (var relationship in incident)
            {
                await RemoveRelationshipAsync(Find(2, relationship.Id, context.Snapshot)!.Value, bracket, context, cancellationToken).ConfigureAwait(false);
            }
            var stored = node.Value.Record.Node!.Value;
            foreach (var definition in indexes)
            {
                if (stored.Labels.Contains(definition.Label!, StringComparer.Ordinal) && stored.Properties.TryGetValue(definition.PropertyKey!, out var value))
                {
                    await DeleteIndexEntryAsync(definition.Id, Composite(GraphRecordCodec.Key(value), stored.Id), node.Value.Reference.Location, context, cancellationToken).ConfigureAwait(false);
                }
            }
            Tombstone(bracket, node.Value.Reference, context);
            SaveRegistrations(bracket);
            return true;
        }, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask DeleteRelationshipAsync(ulong id, ITransactionContext context, CancellationToken cancellationToken = default)
    {
        await LockAsync(context, cancellationToken).ConfigureAwait(false);
        EnsureUnchanged(2, id, context);
        if (Find(2, id, context.Snapshot) is not { } relationship) { return; }
        await ApplyAsync(context, async bracket =>
        {
            await RemoveRelationshipAsync(relationship, bracket, context, cancellationToken).ConfigureAwait(false);
            SaveRegistrations(bracket);
            return true;
        }, cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask RemoveRelationshipAsync(Found found, IStorageTransaction bracket,
        ITransactionContext context, CancellationToken cancellationToken)
    {
        var relationship = found.Record.Relationship!.Value;
        await DeleteIndexEntryAsync(AdjacencyId, Composite(IndexKey.FromUInt64(relationship.SourceId), relationship.Id), found.Reference.Location, context, cancellationToken).ConfigureAwait(false);
        if (relationship.SourceId != relationship.TargetId)
        {
            await DeleteIndexEntryAsync(AdjacencyId, Composite(IndexKey.FromUInt64(relationship.TargetId), relationship.Id), found.Reference.Location, context, cancellationToken).ConfigureAwait(false);
        }
        Tombstone(bracket, found.Reference, context);
    }

    private ulong Insert(IStorageTransaction bracket, byte[] bytes, ITransactionContext context)
    {
        var location = _storage.InsertOwned(bracket, 2, bytes);
        _coordinator.VersionStore.RecordCreated(context.Sequence, location.PageId, location.SlotIndex);
        return GraphStorage.PackLocation(location.PageId, location.SlotIndex);
    }

    private void Tombstone(IStorageTransaction bracket, Reference reference, ITransactionContext context)
    {
        var bytes = _storage.ReadEntry(reference.PageId, reference.SlotIndex);
        _storage.UpdateEntry(bracket, reference.PageId, reference.SlotIndex, RecordVersionStamp.WithDeleter(bytes.Span, context.Sequence));
        _coordinator.VersionStore.RecordTombstoned(context.Sequence, reference.PageId, reference.SlotIndex);
    }

    private void Add(GraphRecord record, ulong location, TransactionSequence writer)
    {
        var (page, slot) = GraphStorage.UnpackLocation(location);
        lock (_sync) { _records[(record.Kind, record.Id)] = new Reference(page, slot, writer); }
    }

    private ulong[] Ids(byte kind)
    {
        lock (_sync) { return _records.Keys.Where(key => key.Kind == kind).Select(key => key.Id).ToArray(); }
    }

    private Found? Find(byte kind, ulong id, TransactionSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        lock (_sync)
        {
            if (!_records.TryGetValue((kind, id), out var reference)) { return null; }
            var found = ReadAt(reference.Location, snapshot);
            if (found is not { } record || record.Reference.Writer != reference.Writer || record.Record.Kind != kind || record.Record.Id != id) { return null; }
            return found;
        }
    }

    private Found? ReadAt(ulong location, TransactionSnapshot snapshot)
    {
        var (page, slot) = GraphStorage.UnpackLocation(location);
        ReadOnlyMemory<byte> bytes;
        if (!_storage.FreeSpaceMap.IsAllocated(page)) { return null; }
        try
        {
            using var handle = _storage.PageManager.GetPage(page);
            if (handle.Page.Type != PageType.Data || handle.Page.OwnerId != 2) { return null; }
            bytes = _storage.ReadEntry(page, slot);
        }
        catch (SlottedPageException) { return null; }
        catch (ArgumentOutOfRangeException) { return null; }
        catch (StorageIOException) when (!_storage.FreeSpaceMap.IsAllocated(page)) { return null; }
        if (bytes.Length < 26) { throw new StorageCorruptionException("Truncated graph record."); }
        var (writer, deleter) = RecordVersionStamp.ReadStamps(bytes.Span);
        if (!snapshot.IsVisible(writer) || (deleter != TransactionSequence.None && snapshot.IsVisible(deleter))) { return null; }
        return new Found(new Reference(page, slot, writer), GraphRecordCodec.Decode(bytes));
    }

    private async ValueTask LockAsync(ITransactionContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.State != TransactionState.Active) { throw new InvalidOperationException("Graph mutation requires an active transaction."); }
        await _coordinator.LockManager.AcquireAsync(context.Sequence, LockResource.Database(), LockMode.Exclusive, cancellationToken).ConfigureAwait(false);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (context.State != TransactionState.Active)
            {
                throw new TransactionAbortedException("Graph transaction ended while waiting for the writer lock.");
            }
        }
        catch
        {
            _coordinator.LockManager.ReleaseAll(context.Sequence);
            throw;
        }
    }

    private TransactionSnapshot Latest(ITransactionContext context) => new(context.Sequence, TransactionSequence.None,
        new TransactionSequence(ulong.MaxValue), _coordinator.GetOpenContexts().Where(item => item.State == TransactionState.Active).Select(item => item.Sequence));

    private void EnsureUnchanged(byte kind, ulong id, ITransactionContext context)
    {
        var prior = Find(kind, id, context.Snapshot);
        var latest = Find(kind, id, Latest(context));
        if (prior?.Reference != latest?.Reference)
        {
            throw new TransactionAbortedException("Graph write conflict: the addressed record changed after the transaction snapshot.");
        }
    }

    private async ValueTask<T> ApplyAsync<T>(ITransactionContext context, Func<IStorageTransaction, ValueTask<T>> apply, CancellationToken cancellationToken)
    {
        try { return await _coordinator.ApplyStatementAsync(context, apply, durable: false, cancellationToken: cancellationToken).ConfigureAwait(false); }
        catch { OpenIndexes(); throw; }
    }

    private readonly record struct Reference(PageId PageId, int SlotIndex, TransactionSequence Writer)
    {
        internal ulong Location => GraphStorage.PackLocation(PageId, SlotIndex);
    }
    private readonly record struct Found(Reference Reference, GraphRecord Record);
}
