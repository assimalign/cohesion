using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Assimalign.Cohesion.Database.Indexing;
using Assimalign.Cohesion.Database.Storage;
using Assimalign.Cohesion.Database.Transactions;

namespace Assimalign.Cohesion.Database.Graph.Storage.Internal;

internal sealed partial class DefaultGraphStore
{
    private const ulong AdjacencyId = ulong.MaxValue;
    private const string TreeName = "graph";
    private readonly Dictionary<ulong, (PageId Page, int Slot, long Root)> _registrations = new();
    private IIndexManager _indexes = null!;

    public bool HasIndex(string label, string propertyKey, TransactionSnapshot snapshot) => Definition(label, propertyKey, snapshot) is not null;

    public async ValueTask CreateIndexAsync(string label, string propertyKey, ITransactionContext context, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(label);
        ArgumentException.ThrowIfNullOrWhiteSpace(propertyKey);
        await LockAsync(context, cancellationToken).ConfigureAwait(false);
        var latest = Latest(context);
        if (Definition(label, propertyKey, latest) is not null) { throw new InvalidOperationException($"A node-property index already exists for '{label}.{propertyKey}'."); }
        ulong id = (ulong)_storage.ReserveTransactionSequence();
        var record = new GraphRecord(3, id, Label: label, PropertyKey: propertyKey);
        byte[] bytes = GraphRecordCodec.Encode(record, context.Sequence);
        var entries = GetNodes(label, latest).Where(node => node.Properties.ContainsKey(propertyKey))
            .Select(node => (Found: Find(1, node.Id, latest)!.Value, Key: Composite(GraphRecordCodec.Key(node.Properties[propertyKey]), node.Id))).ToArray();
        var inserted = await ApplyAsync(context, async bracket =>
        {
            var index = await _indexes.CreateIndexAsync(context, id, new IndexDefinition(TreeName), cancellationToken).ConfigureAwait(false);
            foreach (var entry in entries)
            {
                await index.InsertVersionAsync(bracket, entry.Key, entry.Found.Reference.Location,
                    entry.Found.Reference.Writer, TransactionSequence.None, cancellationToken).ConfigureAwait(false);
            }
            var location = Insert(bracket, bytes, context);
            SaveRegistrations(bracket);
            return location;
        }, cancellationToken).ConfigureAwait(false);
        Add(record, inserted, context.Sequence);
    }

    public async ValueTask DropIndexAsync(string label, string propertyKey, ITransactionContext context, CancellationToken cancellationToken = default)
    {
        await LockAsync(context, cancellationToken).ConfigureAwait(false);
        var definition = Definition(label, propertyKey, context.Snapshot)
            ?? throw new InvalidOperationException($"No visible node-property index exists for '{label}.{propertyKey}'.");
        if (Definition(label, propertyKey, Latest(context))?.Id != definition.Id)
        {
            throw new TransactionAbortedException("Graph write conflict: the index changed after the transaction snapshot.");
        }
        var found = Find(3, definition.Id, context.Snapshot)!.Value;
        await ApplyAsync(context, bracket =>
        {
            Tombstone(bracket, found.Reference, context);
            return new ValueTask<bool>(true);
        }, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<IReadOnlyList<StoredGraphNode>> SearchIndexAsync(string label, string propertyKey, object? value,
        TransactionSnapshot snapshot, CancellationToken cancellationToken = default)
    {
        var definition = Definition(label, propertyKey, snapshot)
            ?? throw new InvalidOperationException($"No visible node-property index exists for '{label}.{propertyKey}'.");
        var key = GraphRecordCodec.Key(value);
        var result = new List<StoredGraphNode>();
        await using var cursor = ResolveIndex(definition.Id).OpenCursor(snapshot,
            new IndexKeyRange(Composite(key, 0), Composite(key, ulong.MaxValue), true, true));
        while (await cursor.MoveNextAsync(cancellationToken).ConfigureAwait(false))
        {
            if (ReadAt(cursor.CurrentEntryReference, snapshot)?.Record.Node is { } node
                && node.Labels.Contains(label, StringComparer.Ordinal) && node.Properties.TryGetValue(propertyKey, out var actual)
                && GraphRecordCodec.ScalarEquals(actual, value) && cursor.CurrentKey.Equals(Composite(key, node.Id)))
            {
                result.Add(node);
            }
        }
        return result;
    }

    public async ValueTask RecoverIndexesAsync(IReadOnlySet<TransactionSequence> writers, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(writers);
        using var bracket = _storage.BeginTransaction();
        await _indexes.PurgeWritersAsync(bracket, writers, cancellationToken).ConfigureAwait(false);
        SaveRegistrations(bracket);
        bracket.Commit();
    }

    private GraphRecord[] Definitions(TransactionSnapshot snapshot)
        => Ids(3).Select(id => Find(3, id, snapshot)).Where(found => found.HasValue).Select(found => found!.Value.Record).ToArray();

    private GraphRecord? Definition(string label, string propertyKey, TransactionSnapshot snapshot)
    {
        foreach (var definition in Definitions(snapshot))
        {
            if (definition.Label == label && definition.PropertyKey == propertyKey) { return definition; }
        }
        return null;
    }

    private async ValueTask EnsureAdjacencyAsync(ITransactionContext context, CancellationToken cancellationToken)
    {
        if (!_indexes.TryGetIndex(AdjacencyId, TreeName, out _))
        {
            await _indexes.CreateIndexAsync(context, AdjacencyId, new IndexDefinition(TreeName), cancellationToken).ConfigureAwait(false);
        }
    }

    private IIndex ResolveIndex(ulong id) => _indexes.TryGetIndex(id, TreeName, out var index)
        ? index : throw new StorageCorruptionException($"Missing graph B+Tree registration '{id}'.");

    private static IndexKey Composite(IndexKey prefix, ulong id)
    {
        var bytes = new byte[prefix.Length + 8];
        prefix.Encoded.Span.CopyTo(bytes);
        BinaryPrimitives.WriteUInt64BigEndian(bytes.AsSpan(prefix.Length), id);
        return new IndexKey(bytes);
    }

    private async ValueTask InsertIndexAsync(ulong id, IndexKey key, ulong location, ITransactionContext context, CancellationToken cancellationToken)
    {
        await ResolveIndex(id).InsertAsync(context, key, location, cancellationToken).ConfigureAwait(false);
        _coordinator.VersionStore.RecordIndexEntryCreated(context.Sequence, new IndexUndo(this, id), key.Encoded, location);
    }

    private async ValueTask DeleteIndexEntryAsync(ulong id, IndexKey key, ulong location, ITransactionContext context, CancellationToken cancellationToken)
    {
        await ResolveIndex(id).DeleteAsync(context, key, location, cancellationToken).ConfigureAwait(false);
        _coordinator.VersionStore.RecordIndexEntryTombstoned(context.Sequence, new IndexUndo(this, id), key.Encoded, location);
    }

    private void OpenIndexes()
    {
        _registrations.Clear();
        var registrations = new List<BTreeIndexRegistration>();
        using var iterator = _storage.GetUnitIterator(1);
        while (iterator.MoveNext())
        {
            var unit = iterator.Current;
            if (unit.Data.Length != 34) { throw new StorageCorruptionException("Invalid graph B+Tree registration length."); }
            using var stream = new MemoryStream(unit.Data.ToArray(), false);
            using var reader = new BinaryReader(stream);
            if (reader.ReadUInt64() != 0 || reader.ReadUInt64() != 0 || reader.ReadByte() != 4 || reader.ReadByte() != 1)
            {
                throw new StorageCorruptionException("Invalid graph B+Tree registration header.");
            }
            ulong id = reader.ReadUInt64();
            long root = reader.ReadInt64();
            if (id == 0 || root <= 0 || _registrations.ContainsKey(id)) { throw new StorageCorruptionException("Invalid graph B+Tree registration identity."); }
            registrations.Add(new BTreeIndexRegistration(id, new IndexDefinition(TreeName), root));
            _registrations.Add(id, (unit.PageId, unit.SlotIndex, root));
        }
        _indexes = BTreeIndexManager.Create(new BTreeIndexManagerOptions
        {
            Storage = _storage,
            TransactionSource = new TransactionSource(_coordinator),
            ExistingIndexes = registrations
        });
    }

    private void SaveRegistrations(IStorageTransaction bracket)
    {
        foreach (var registration in ((IIndexRegistry)_indexes).ExportRegistrations())
        {
            if (_registrations.TryGetValue(registration.ObjectId, out var prior) && prior.Root == registration.RootPageId) { continue; }
            using var stream = new MemoryStream();
            using var writer = new BinaryWriter(stream, Encoding.UTF8, true);
            writer.Write(0UL); writer.Write(0UL); writer.Write((byte)4); writer.Write((byte)1);
            writer.Write(registration.ObjectId); writer.Write(registration.RootPageId);
            if (_registrations.ContainsKey(registration.ObjectId))
            {
                _storage.UpdateEntry(bracket, prior.Page, prior.Slot, stream.ToArray());
                _registrations[registration.ObjectId] = (prior.Page, prior.Slot, registration.RootPageId);
            }
            else
            {
                var location = _storage.InsertOwned(bracket, 1, stream.ToArray());
                _registrations[registration.ObjectId] = (location.PageId, location.SlotIndex, registration.RootPageId);
            }
        }
    }

    private sealed class TransactionSource : IStorageTransactionSource
    {
        private readonly TransactionCoordinator _coordinator;

        /// <summary>Initializes a new instance of the <see cref="TransactionSource"/> class.</summary>
        /// <param name="coordinator">The coordinator that owns the shared statement brackets.</param>
        public TransactionSource(TransactionCoordinator coordinator)
        {
            _coordinator = coordinator;
        }

        public IStorageTransaction GetStorageTransaction(ITransactionContext context) => _coordinator.TryGetStorageTransaction(context, out var bracket)
            ? bracket : throw new InvalidOperationException("Graph index mutation requires a shared statement bracket.");
    }

    private sealed class IndexUndo : IRecordVersionIndex
    {
        private readonly DefaultGraphStore _store;
        private readonly ulong _id;

        /// <summary>Initializes a new instance of the <see cref="IndexUndo"/> class.</summary>
        /// <param name="store">The graph store whose index registry resolves the index.</param>
        /// <param name="id">The identity of the index the undo entries target.</param>
        public IndexUndo(DefaultGraphStore store, ulong id)
        {
            _store = store;
            _id = id;
        }

        public ValueTask EraseAsync(IStorageTransaction transaction, ReadOnlyMemory<byte> key, ulong entryReference,
            TransactionSequence writer, CancellationToken cancellationToken = default)
            => _store._indexes.TryGetIndex(_id, TreeName, out var index)
                ? index.EraseAsync(transaction, new IndexKey(key), entryReference, writer, cancellationToken) : default;
        public ValueTask ClearDeleterAsync(IStorageTransaction transaction, ReadOnlyMemory<byte> key, ulong entryReference,
            TransactionSequence writer, CancellationToken cancellationToken = default)
            => _store._indexes.TryGetIndex(_id, TreeName, out var index)
                ? index.ClearDeleterAsync(transaction, new IndexKey(key), entryReference, writer, cancellationToken) : default;
    }
}
