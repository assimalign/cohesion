using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Assimalign.Cohesion.Database.Graph.Storage.Internal;
using Assimalign.Cohesion.Database.Indexing;
using Assimalign.Cohesion.Database.Storage;
using Assimalign.Cohesion.Database.Transactions;

namespace Assimalign.Cohesion.Database.Graph.Storage;

public sealed partial class GraphStore
{
    private const ulong AdjacencyId = ulong.MaxValue;
    private const string TreeName = "graph";
    private readonly Dictionary<ulong, (PageId Page, int Slot, long Root)> _registrations = new();
    private BTreeIndexManager _indexes = null!;

    /// <summary>Tests whether an exact property index is visible.</summary>
    /// <param name="label">Node label.</param><param name="propertyKey">Property name.</param><param name="snapshot">Visibility snapshot.</param><returns>True when an index is visible.</returns>
    public bool HasIndex(string label, string propertyKey, TransactionSnapshot snapshot) => Definition(label, propertyKey, snapshot) is not null;

    /// <summary>
    /// Lists every exact property index visible to a snapshot in one pass over the index
    /// definitions, so a planner can match many labels and property keys without a lookup per pair.
    /// </summary>
    /// <param name="snapshot">Visibility snapshot.</param><returns>The visible indexes, each label/property pair once.</returns>
    public IReadOnlyList<StoredGraphIndex> GetIndexes(TransactionSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var definitions = Definitions(snapshot);
        var indexes = new StoredGraphIndex[definitions.Length];
        for (int i = 0; i < definitions.Length; i++) { indexes[i] = new StoredGraphIndex(definitions[i].Label!, definitions[i].PropertyKey!); }
        return indexes;
    }

    /// <summary>Builds a transactional B+Tree for a node label and property.</summary>
    /// <param name="label">Node label.</param><param name="propertyKey">Property name.</param><param name="context">Owning transaction.</param><param name="cancellationToken">Cancellation token.</param><returns>A task representing index creation.</returns>
    /// <exception cref="InvalidOperationException">An index already exists for this label/property pair.</exception>
    /// <exception cref="ArgumentException">The label or property name is null or whitespace.</exception>
    /// <exception cref="GraphElementTooLargeException">The names exceed one graph record, or an existing node's value exceeds the index key.</exception>
    public async ValueTask CreateIndexAsync(string label, string propertyKey, TransactionContext context, CancellationToken cancellationToken = default)
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

    /// <summary>Drops a node-property index definition; older snapshots retain its tree.</summary>
    /// <param name="label">Node label.</param><param name="propertyKey">Property name.</param><param name="context">Owning transaction.</param><param name="cancellationToken">Cancellation token.</param><returns>A task representing index deletion.</returns>
    /// <exception cref="InvalidOperationException">No visible index exists for this label/property pair.</exception>
    /// <exception cref="TransactionAbortedException">The index changed after the transaction snapshot.</exception>
    public async ValueTask DropIndexAsync(string label, string propertyKey, TransactionContext context, CancellationToken cancellationToken = default)
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

    /// <summary>Seeks an exact scalar property value through its B+Tree.</summary>
    /// <param name="label">Node label.</param><param name="propertyKey">Property name.</param><param name="value">Scalar value.</param><param name="snapshot">Visibility snapshot.</param><param name="cancellationToken">Cancellation token.</param><returns>The matching visible nodes.</returns>
    /// <exception cref="InvalidOperationException">No visible index exists for this label/property pair.</exception>
    /// <exception cref="ArgumentException">The scalar bound is not a supported scalar.</exception>
    /// <remarks>A value too long for the index key matches nothing: no write can store one.</remarks>
    public async ValueTask<IReadOnlyList<StoredGraphNode>> SearchIndexAsync(string label, string propertyKey, object? value,
        TransactionSnapshot snapshot, CancellationToken cancellationToken = default)
    {
        var definition = Definition(label, propertyKey, snapshot)
            ?? throw new InvalidOperationException($"No visible node-property index exists for '{label}.{propertyKey}'.");
        // Every write and backfill refuses a value whose key does not fit, so no entry, and no
        // indexed node, holds one: the search matches nothing rather than failing the query.
        if (!GraphRecordCodec.TryKey(value, out var key)) { return []; }
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

    /// <summary>Scrubs unproven index writers after coordinator record recovery and before checkpoint.</summary>
    /// <param name="writers">Aborted or uncommitted writers.</param><param name="cancellationToken">Cancellation token.</param><returns>A task representing recovery.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="writers"/> is null.</exception>
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

    private async ValueTask EnsureAdjacencyAsync(TransactionContext context, CancellationToken cancellationToken)
    {
        if (!_indexes.TryGetIndex(AdjacencyId, TreeName, out _))
        {
            await _indexes.CreateIndexAsync(context, AdjacencyId, new IndexDefinition(TreeName), cancellationToken).ConfigureAwait(false);
        }
    }

    private BTreeIndex ResolveIndex(ulong id) => _indexes.TryGetIndex(id, TreeName, out var index)
        ? index : throw new StorageCorruptionException($"Missing graph B+Tree registration '{id}'.");

    private static IndexKey Composite(IndexKey prefix, ulong id)
    {
        var bytes = new byte[prefix.Length + 8];
        prefix.Encoded.Span.CopyTo(bytes);
        BinaryPrimitives.WriteUInt64BigEndian(bytes.AsSpan(prefix.Length), id);
        return new IndexKey(bytes);
    }

    private async ValueTask InsertIndexAsync(ulong id, IndexKey key, ulong location, TransactionContext context, CancellationToken cancellationToken)
    {
        await ResolveIndex(id).InsertAsync(context, key, location, cancellationToken).ConfigureAwait(false);
        _coordinator.VersionStore.RecordIndexEntryCreated(context.Sequence, new IndexUndo(this, id), key.Encoded, location);
    }

    private async ValueTask DeleteIndexEntryAsync(ulong id, IndexKey key, ulong location, TransactionContext context, CancellationToken cancellationToken)
    {
        await ResolveIndex(id).DeleteAsync(context, key, location, cancellationToken).ConfigureAwait(false);
        _coordinator.VersionStore.RecordIndexEntryTombstoned(context.Sequence, new IndexUndo(this, id), key.Encoded, location);
    }

    private void OpenIndexes()
    {
        _registrations.Clear();
        var registrations = new List<BTreeIndexRegistration>();
        foreach (var (registration, page, slot) in ReadRegistrations(_storage))
        {
            registrations.Add(registration);
            _registrations.Add(registration.ObjectId, (page, slot, registration.RootPageId));
        }
        _indexes = BTreeIndexManager.Create(new BTreeIndexManagerOptions
        {
            Storage = _storage,
            TransactionSource = ResolveStatementBracket,
            ExistingIndexes = registrations
        });
    }

    private static List<(BTreeIndexRegistration Registration, PageId Page, int Slot)> ReadRegistrations(GraphStorage storage)
    {
        var registrations = new List<(BTreeIndexRegistration, PageId, int)>();
        var ids = new HashSet<ulong>();
        using var iterator = storage.GetUnitIterator(1);
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
            if (id == 0 || root <= 0 || !ids.Add(id)) { throw new StorageCorruptionException("Invalid graph B+Tree registration identity."); }
            registrations.Add((new BTreeIndexRegistration(id, new IndexDefinition(TreeName), root), unit.PageId, unit.SlotIndex));
        }
        return registrations;
    }

    private void SaveRegistrations(StorageTransaction bracket)
    {
        foreach (var registration in _indexes.ExportRegistrations())
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

    // The index manager's storage transaction for a context: the shared statement bracket the
    // coordinator owns, or this store's own error when there is none.
    private StorageTransaction ResolveStatementBracket(TransactionContext context) => _coordinator.TryGetStorageTransaction(context, out var bracket)
        ? bracket : throw new InvalidOperationException("Graph index mutation requires a shared statement bracket.");

    private sealed class IndexUndo : RecordVersionIndex
    {
        private readonly GraphStore _store;
        private readonly ulong _id;

        /// <summary>Initializes a new instance of the <see cref="IndexUndo"/> class.</summary>
        /// <param name="store">The graph store whose index registry resolves the index.</param>
        /// <param name="id">The identity of the index the undo entries target.</param>
        public IndexUndo(GraphStore store, ulong id)
        {
            _store = store;
            _id = id;
        }

        protected override ValueTask EraseCoreAsync(StorageTransaction transaction, ReadOnlyMemory<byte> key, ulong entryReference,
            TransactionSequence writer, CancellationToken cancellationToken)
            => _store._indexes.TryGetIndex(_id, TreeName, out var index)
                ? index.EraseAsync(transaction, new IndexKey(key), entryReference, writer, cancellationToken) : default;
        protected override ValueTask ClearDeleterCoreAsync(StorageTransaction transaction, ReadOnlyMemory<byte> key, ulong entryReference,
            TransactionSequence writer, CancellationToken cancellationToken)
            => _store._indexes.TryGetIndex(_id, TreeName, out var index)
                ? index.ClearDeleterAsync(transaction, new IndexKey(key), entryReference, writer, cancellationToken) : default;
    }
}
