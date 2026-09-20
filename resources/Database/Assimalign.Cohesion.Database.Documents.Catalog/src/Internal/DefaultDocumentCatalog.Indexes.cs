using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Assimalign.Cohesion.Database.Documents.Storage;
using Assimalign.Cohesion.Database.Indexing;
using Assimalign.Cohesion.Database.Storage;
using Assimalign.Cohesion.Database.Transactions;

namespace Assimalign.Cohesion.Database.Documents.Catalog;

internal sealed partial class DefaultDocumentCatalog
{
    private readonly Dictionary<(Guid CollectionId, string Name), List<Reference>> _indexDefinitions = new();
    private readonly Dictionary<ulong, (PageId Page, int Slot, long Root)> _registrations = new();
    private IIndexManager _indexes = null!;

    public IReadOnlyList<DocumentIndexMetadata> GetIndexes(Guid collectionId, TransactionSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        lock (_sync)
        {
            var result = new List<DocumentIndexMetadata>();
            foreach (var key in _indexDefinitions.Keys.Where(key => key.CollectionId == collectionId)
                .OrderBy(key => key.Name, StringComparer.Ordinal).ToArray())
            {
                if (FindIndex(key.CollectionId, key.Name, snapshot) is { } found)
                {
                    result.Add(found.Record.Index!.Value);
                }
            }
            return result;
        }
    }

    public async ValueTask<DocumentIndexMetadata> CreateIndexAsync(Guid collectionId, string name, string path,
        ITransactionContext context, CancellationToken cancellationToken = default)
    {
        EnsureActive(context);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        DocumentIndexKeys.ValidatePath(path);
        if (!GetCollections(context.Snapshot).Any(collection => collection.Id == collectionId))
        {
            throw new DocumentCatalogException("The collection does not exist.");
        }
        lock (_sync)
        {
            if (FindIndex(collectionId, name, context.Snapshot) is not null)
            {
                throw new DocumentCatalogException($"Index '{name}' already exists.");
            }
        }
        var metadata = new DocumentIndexMetadata(collectionId, name, path, (ulong)_storage.ReserveTransactionSequence());
        var encoded = DocumentCatalogCodec.Encode(new CatalogRecord(null, null, metadata), context.Sequence);
        var entries = new List<(IndexKey Key, Reference Reference)>();
        lock (_sync)
        {
            foreach (var document in GetDocuments(collectionId, null, context.Snapshot))
            {
                var found = FindDocumentVersion(collectionId, document.Id, context.Snapshot)!.Value;
                if (KeyFor(document, path) is { } key)
                {
                    entries.Add((key, found.Reference));
                }
            }
        }
        var location = await ApplyAsync(context, async bracket =>
        {
            var index = await _indexes.CreateIndexAsync(context, metadata.ObjectId, new IndexDefinition(name), cancellationToken).ConfigureAwait(false);
            foreach (var entry in entries)
            {
                // The definition is invisible to older snapshots; preserve source stamps
                // so a newly visible definition returns the same rows as a record scan.
                await index.InsertVersionAsync(bracket, entry.Key,
                    DocumentStorage.PackLocation(entry.Reference.PageId, entry.Reference.SlotIndex),
                    entry.Reference.Writer, TransactionSequence.None, cancellationToken).ConfigureAwait(false);
            }
            var inserted = _storage.InsertEntry(bracket, encoded);
            _coordinator.VersionStore.RecordCreated(context.Sequence, inserted.PageId, inserted.SlotIndex);
            SaveRegistrations(bracket);
            return inserted;
        }, cancellationToken).ConfigureAwait(false);
        lock (_sync)
        {
            Add(new CatalogRecord(null, null, metadata), new Reference(location.PageId, location.SlotIndex, context.Sequence));
        }
        return metadata;
    }

    public async ValueTask DeleteIndexAsync(Guid collectionId, string name, ITransactionContext context,
        CancellationToken cancellationToken = default)
    {
        Found? previous;
        lock (_sync)
        {
            previous = FindIndex(collectionId, name, context.Snapshot);
        }
        if (previous is null)
        {
            throw new DocumentCatalogException($"Index '{name}' does not exist.");
        }
        await DeleteAsync(previous, context, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<IReadOnlyList<DocumentCatalogEntry>> SearchIndexAsync(Guid collectionId, string indexName,
        object? lower, bool includeLower, object? upper, bool includeUpper, TransactionSnapshot snapshot,
        CancellationToken cancellationToken = default)
    {
        DocumentIndexMetadata definition;
        lock (_sync)
        {
            definition = FindIndex(collectionId, indexName, snapshot)?.Record.Index
                ?? throw new DocumentCatalogException($"Index '{indexName}' is not visible.");
        }
        var index = ResolveIndex(definition);
        var range = new IndexKeyRange(lower is null ? null : DocumentIndexKeys.Encode(lower),
            upper is null ? null : DocumentIndexKeys.Encode(upper), includeLower, includeUpper);
        var result = new Dictionary<string, DocumentCatalogEntry>(StringComparer.Ordinal);
        await using var cursor = index.OpenCursor(snapshot, range);
        while (await cursor.MoveNextAsync(cancellationToken).ConfigureAwait(false))
        {
            var (page, slot) = DocumentStorage.UnpackLocation(cursor.CurrentEntryReference);
            var bytes = _storage.ReadEntry(page, slot);
            var record = DocumentCatalogCodec.Decode(bytes.Span);
            var (writer, deleter) = RecordVersionStamp.ReadStamps(bytes.Span);
            if (record.Document is { } document && document.CollectionId == collectionId
                && snapshot.IsVisible(writer) && (deleter == TransactionSequence.None || !snapshot.IsVisible(deleter)))
            {
                result[document.Id] = document;
            }
        }
        return result.Values.OrderBy(document => document.Id, StringComparer.Ordinal).ToArray();
    }

    public async ValueTask RecoverIndexesAsync(IReadOnlySet<TransactionSequence> writers, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(writers);
        using var bracket = _storage.BeginTransaction();
        await _indexes.PurgeWritersAsync(bracket, writers, cancellationToken).ConfigureAwait(false);
        SaveRegistrations(bracket);
        bracket.Commit();
    }

    private Found? FindIndex(Guid collectionId, string name, TransactionSnapshot snapshot)
    {
        return _indexDefinitions.TryGetValue((collectionId, name), out var versions)
            ? Find(versions, snapshot, record => record.Index is { } index && index.CollectionId == collectionId && index.Name == name)
            : null;
    }

    private List<IndexChange> PrepareIndexChanges(DocumentCatalogEntry? document, Found? previous, TransactionSnapshot snapshot)
    {
        var priorDocument = previous?.Record.Document;
        if (document is null && priorDocument is null)
        {
            return [];
        }
        var collectionId = document?.CollectionId ?? priorDocument!.Value.CollectionId;
        var result = new List<IndexChange>();
        foreach (var metadata in GetIndexes(collectionId, snapshot))
        {
            result.Add(new IndexChange(metadata,
                priorDocument is { } old ? KeyFor(old, metadata.Path) : null,
                document is { } current ? KeyFor(current, metadata.Path) : null,
                previous is { } prior ? DocumentStorage.PackLocation(prior.Reference.PageId, prior.Reference.SlotIndex) : 0));
        }
        return result;
    }

    private IndexKey? KeyFor(DocumentCatalogEntry document, string path)
    {
        var key = DocumentIndexKeys.Read(_storage.ReadContent(new DocumentContentReference(document.HeadLocation, document.Length, document.Checksum)), path);
        if (key is { Length: > 1024 })
        {
            throw new DocumentCatalogException("Indexed scalar encodes beyond the shared B+Tree 1024-byte key limit.");
        }
        return key;
    }

    private async ValueTask ApplyIndexChangesAsync(List<IndexChange> changes, (PageId PageId, int SlotIndex) inserted,
        ITransactionContext context, CancellationToken cancellationToken)
    {
        foreach (var change in changes)
        {
            var index = ResolveIndex(change.Metadata);
            var undo = new IndexUndo(this, change.Metadata);
            if (change.OldKey is { } oldKey)
            {
                await index.DeleteAsync(context, oldKey, change.OldLocation, cancellationToken).ConfigureAwait(false);
                _coordinator.VersionStore.RecordIndexEntryTombstoned(context.Sequence, undo, oldKey.Encoded, change.OldLocation);
            }
            if (change.NewKey is { } newKey)
            {
                ulong location = DocumentStorage.PackLocation(inserted.PageId, inserted.SlotIndex);
                await index.InsertAsync(context, newKey, location, cancellationToken).ConfigureAwait(false);
                _coordinator.VersionStore.RecordIndexEntryCreated(context.Sequence, undo, newKey.Encoded, location);
            }
        }
    }

    private IIndex ResolveIndex(DocumentIndexMetadata metadata)
        => _indexes.TryGetIndex(metadata.ObjectId, metadata.Name, out var index)
            ? index : throw new DocumentCatalogException($"Missing physical tree for index '{metadata.Name}'.");

    private async ValueTask<T> ApplyAsync<T>(ITransactionContext context,
        Func<IStorageTransaction, ValueTask<T>> apply, CancellationToken cancellationToken)
    {
        try
        {
            return await _coordinator.ApplyStatementAsync(context, apply, durable: false, cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            // Physical rollback restores pages; reattach the manager's in-memory roots
            // from those restored registrations before logical undo or another write.
            OpenIndexes();
            throw;
        }
    }

    private void OpenIndexes()
    {
        _registrations.Clear();
        var registrations = new List<BTreeIndexRegistration>();
        using var iterator = _storage.GetUnitIterator(1);
        while (iterator.MoveNext())
        {
            var unit = iterator.Current;
            using var stream = new MemoryStream(unit.Data.ToArray(), writable: false);
            using var reader = new BinaryReader(stream, new UTF8Encoding(false, true));
            if (reader.ReadUInt64() != 0 || reader.ReadUInt64() != 0 || reader.ReadByte() != 5 || reader.ReadByte() != 1)
            {
                throw new DocumentCatalogException("Invalid physical index registration header.");
            }
            ulong objectId = reader.ReadUInt64();
            long root = reader.ReadInt64();
            string name = DocumentCatalogCodec.ReadString(reader) ?? throw new DocumentCatalogException("Missing physical index name.");
            if (objectId == 0 || root <= 0 || stream.Position != stream.Length || _registrations.ContainsKey(objectId))
            {
                throw new DocumentCatalogException("Invalid physical index registration.");
            }
            registrations.Add(new BTreeIndexRegistration(objectId, new IndexDefinition(name), root));
            _registrations.Add(objectId, (unit.PageId, unit.SlotIndex, root));
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
            if (_registrations.TryGetValue(registration.ObjectId, out var prior) && prior.Root == registration.RootPageId)
            {
                continue;
            }
            using var stream = new MemoryStream();
            using var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);
            writer.Write(0UL);
            writer.Write(0UL);
            writer.Write((byte)5);
            writer.Write((byte)1);
            writer.Write(registration.ObjectId);
            writer.Write(registration.RootPageId);
            DocumentCatalogCodec.WriteString(writer, registration.Definition.Name);
            var bytes = stream.ToArray();
            if (_registrations.ContainsKey(registration.ObjectId))
            {
                _storage.UpdateEntry(bracket, prior.Page, prior.Slot, bytes);
                _registrations[registration.ObjectId] = (prior.Page, prior.Slot, registration.RootPageId);
            }
            else
            {
                var location = _storage.InsertIndexRegistration(bracket, bytes);
                _registrations[registration.ObjectId] = (location.PageId, location.SlotIndex, registration.RootPageId);
            }
        }
    }

    private readonly record struct IndexChange(DocumentIndexMetadata Metadata, IndexKey? OldKey, IndexKey? NewKey, ulong OldLocation);

    private sealed class TransactionSource(TransactionCoordinator coordinator) : IStorageTransactionSource
    {
        public IStorageTransaction GetStorageTransaction(ITransactionContext context)
            => coordinator.TryGetStorageTransaction(context, out var bracket)
                ? bracket : throw new InvalidOperationException("Index mutation requires a shared statement bracket.");
    }

    private sealed class IndexUndo(DefaultDocumentCatalog catalog, DocumentIndexMetadata metadata) : IRecordVersionIndex
    {
        public ValueTask EraseAsync(IStorageTransaction transaction, ReadOnlyMemory<byte> key, ulong entryReference,
            TransactionSequence writer, CancellationToken cancellationToken = default)
            => catalog.ResolveIndex(metadata).EraseAsync(transaction, new IndexKey(key), entryReference, writer, cancellationToken);

        public ValueTask ClearDeleterAsync(IStorageTransaction transaction, ReadOnlyMemory<byte> key, ulong entryReference,
            TransactionSequence writer, CancellationToken cancellationToken = default)
            => catalog.ResolveIndex(metadata).ClearDeleterAsync(transaction, new IndexKey(key), entryReference, writer, cancellationToken);
    }
}
