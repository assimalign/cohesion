using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Database.Documents.Storage;
using Assimalign.Cohesion.Database.Storage;
using Assimalign.Cohesion.Database.Transactions;

namespace Assimalign.Cohesion.Database.Documents.Catalog.Internal;

internal sealed partial class DefaultDocumentCatalog : IDocumentCatalog
{
    private readonly DocumentStorage _storage;
    private readonly TransactionCoordinator _coordinator;
    private readonly object _sync = new();
    private readonly Dictionary<string, List<Reference>> _collections = new(StringComparer.Ordinal);
    private readonly Dictionary<(Guid CollectionId, string Name), List<Reference>> _documents = new();

    private DefaultDocumentCatalog(DocumentStorage storage, TransactionCoordinator coordinator)
    {
        _storage = storage;
        _coordinator = coordinator;
    }

    internal static DefaultDocumentCatalog Open(DocumentStorage storage, TransactionCoordinator coordinator)
    {
        var catalog = new DefaultDocumentCatalog(storage, coordinator);
        // Owner zero is exclusively metadata; opening never materializes chunks.
        using var iterator = storage.GetUnitIterator(0);
        while (iterator.MoveNext())
        {
            var unit = iterator.Current;
            var record = DocumentCatalogCodec.Decode(unit.Data.Span);
            var (writer, _) = RecordVersionStamp.ReadStamps(unit.Data.Span);
            catalog.Add(record, new Reference(unit.PageId, unit.SlotIndex, writer));
        }
        catalog.OpenIndexes();
        return catalog;
    }

    public DocumentCollectionMetadata? FindCollection(string name, TransactionSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(snapshot);
        lock (_sync)
        {
            return FindCollectionVersion(name, snapshot)?.Record.Collection;
        }
    }

    public IReadOnlyList<DocumentCollectionMetadata> GetCollections(TransactionSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        lock (_sync)
        {
            var result = new List<DocumentCollectionMetadata>();
            foreach (string name in _collections.Keys.Order(StringComparer.Ordinal).ToArray())
            {
                if (FindCollectionVersion(name, snapshot) is { } found)
                {
                    result.Add(found.Record.Collection!.Value);
                }
            }
            return result;
        }
    }

    public async ValueTask SaveCollectionAsync(DocumentCollectionMetadata collection, ITransactionContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentException.ThrowIfNullOrWhiteSpace(collection.Name);
        if (collection.Id == Guid.Empty)
        {
            throw new ArgumentException("A collection must have a nonempty identity.", nameof(collection));
        }
        if (collection.Owner is not (DatabaseObjectOwner.Adhoc or DatabaseObjectOwner.Schema)
            || (collection.Owner == DatabaseObjectOwner.Schema && string.IsNullOrWhiteSpace(collection.OwningSchema)))
        {
            throw new ArgumentException("A schema-owned collection must name its owning schema.", nameof(collection));
        }

        Found? previous;
        lock (_sync)
        {
            previous = FindCollectionVersion(collection.Name, context.Snapshot);
        }
        await SaveAsync(new CatalogRecord(collection, null), previous, context, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask DeleteCollectionAsync(Guid collectionId, ITransactionContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        Found? previous = null;
        lock (_sync)
        {
            foreach (string name in _collections.Keys.ToArray())
            {
                var found = FindCollectionVersion(name, context.Snapshot);
                if (found?.Record.Collection?.Id == collectionId)
                {
                    previous = found;
                    break;
                }
            }
        }
        await DeleteAsync(previous, context, cancellationToken).ConfigureAwait(false);
    }

    public DocumentCatalogEntry? FindDocument(Guid collectionId, string name, TransactionSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(snapshot);
        lock (_sync)
        {
            return FindDocumentVersion(collectionId, name, snapshot)?.Record.Document;
        }
    }

    public IReadOnlyList<DocumentCatalogEntry> GetDocuments(Guid collectionId, string? prefix, TransactionSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        lock (_sync)
        {
            var result = new List<DocumentCatalogEntry>();
            foreach (var key in _documents.Keys
                .Where(key => key.CollectionId == collectionId && (prefix is null || key.Name.StartsWith(prefix, StringComparison.Ordinal)))
                .OrderBy(key => key.Name, StringComparer.Ordinal).ToArray())
            {
                if (FindDocumentVersion(collectionId, key.Name, snapshot) is { } found)
                {
                    result.Add(found.Record.Document!.Value);
                }
            }
            return result;
        }
    }

    public async ValueTask SaveDocumentAsync(DocumentCatalogEntry document, ITransactionContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentException.ThrowIfNullOrWhiteSpace(document.Name);
        if (document.CollectionId == Guid.Empty || document.Length <= 0 || document.Length > int.MaxValue || document.HeadLocation == 0 || document.Version == 0)
        {
            throw new ArgumentException("Document metadata requires a collection identity and a head for nonempty content.", nameof(document));
        }

        Found? previous;
        lock (_sync)
        {
            previous = FindDocumentVersion(document.CollectionId, document.Name, context.Snapshot);
        }
        await SaveAsync(new CatalogRecord(null, document), previous, context, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask DeleteDocumentAsync(Guid collectionId, string name, ITransactionContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(context);
        Found? previous;
        lock (_sync)
        {
            previous = FindDocumentVersion(collectionId, name, context.Snapshot);
        }
        await DeleteAsync(previous, context, cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask SaveAsync(CatalogRecord record, Found? previous, ITransactionContext context, CancellationToken cancellationToken)
    {
        EnsureActive(context);
        byte[] bytes = DocumentCatalogCodec.Encode(record, context.Sequence);
        var changes = PrepareIndexChanges(record.Document, previous, context.Snapshot);
        var location = await ApplyAsync(context, async bracket =>
        {
            if (previous is { } old)
            {
                Tombstone(bracket, old.Reference, context.Sequence);
            }
            var inserted = _storage.InsertEntry(bracket, bytes);
            _coordinator.VersionStore.RecordCreated(context.Sequence, inserted.PageId, inserted.SlotIndex);
            await ApplyIndexChangesAsync(changes, inserted, context, cancellationToken).ConfigureAwait(false);
            SaveRegistrations(bracket);
            return inserted;
        }, cancellationToken).ConfigureAwait(false);

        // Publish only after the physical bracket succeeds. Readers re-read stamps
        // so transaction rollback, purge and page reuse cannot stale the directory.
        lock (_sync)
        {
            Add(record, new Reference(location.PageId, location.SlotIndex, context.Sequence));
        }
    }

    private async ValueTask DeleteAsync(Found? previous, ITransactionContext context, CancellationToken cancellationToken)
    {
        EnsureActive(context);
        cancellationToken.ThrowIfCancellationRequested();
        if (previous is not { } old)
        {
            return;
        }
        var changes = PrepareIndexChanges(null, previous, context.Snapshot);
        await ApplyAsync(context, async bracket =>
        {
            Tombstone(bracket, old.Reference, context.Sequence);
            await ApplyIndexChangesAsync(changes, default, context, cancellationToken).ConfigureAwait(false);
            SaveRegistrations(bracket);
            return true;
        }, cancellationToken).ConfigureAwait(false);
    }

    private void Tombstone(IStorageTransaction bracket, Reference reference, TransactionSequence writer)
    {
        var bytes = _storage.ReadEntry(reference.PageId, reference.SlotIndex);
        var tombstoned = RecordVersionStamp.WithDeleter(bytes.Span, writer);
        _storage.UpdateEntry(bracket, reference.PageId, reference.SlotIndex, tombstoned);
        _coordinator.VersionStore.RecordTombstoned(writer, reference.PageId, reference.SlotIndex);
    }

    private void Add(CatalogRecord record, Reference reference)
    {
        List<Reference>? versions;
        if (record.Collection is { } collection)
        {
            if (!_collections.TryGetValue(collection.Name, out versions))
            {
                _collections[collection.Name] = versions = new List<Reference>();
            }
        }
        else if (record.Index is { } index)
        {
            var key = (index.CollectionId, index.Name);
            if (!_indexDefinitions.TryGetValue(key, out versions))
            {
                _indexDefinitions[key] = versions = new List<Reference>();
            }
        }
        else
        {
            var document = record.Document!.Value;
            var key = (document.CollectionId, document.Name);
            if (!_documents.TryGetValue(key, out versions))
            {
                _documents[key] = versions = new List<Reference>();
            }
        }
        versions.RemoveAll(item => item.PageId == reference.PageId && item.SlotIndex == reference.SlotIndex);
        versions.Add(reference);
    }

    private Found? FindCollectionVersion(string name, TransactionSnapshot snapshot)
    {
        if (!_collections.TryGetValue(name, out var versions))
        {
            return null;
        }
        var result = Find(versions, snapshot, record => record.Collection?.Name == name);
        if (versions.Count == 0)
        {
            _collections.Remove(name);
        }
        return result;
    }

    private Found? FindDocumentVersion(Guid collectionId, string name, TransactionSnapshot snapshot)
    {
        if (!_documents.TryGetValue((collectionId, name), out var versions))
        {
            return null;
        }
        var result = Find(versions, snapshot, record => record.Document is { } document && document.CollectionId == collectionId && document.Name == name);
        if (versions.Count == 0)
        {
            _documents.Remove((collectionId, name));
        }
        return result;
    }

    private Found? Find(List<Reference> versions, TransactionSnapshot snapshot, Func<CatalogRecord, bool> matches)
    {
        Found? result = null;
        for (int i = versions.Count - 1; i >= 0; i--)
        {
            var reference = versions[i];
            ReadOnlyMemory<byte> bytes;
            try
            {
                bytes = _storage.ReadEntry(reference.PageId, reference.SlotIndex);
            }
            catch (SlottedPageException)
            {
                versions.RemoveAt(i);
                continue;
            }
            catch (ArgumentOutOfRangeException)
            {
                versions.RemoveAt(i);
                continue;
            }
            // CRC and I/O exceptions deliberately propagate. Only reclaimed slots
            // and changed identities invalidate a cached physical reference.
            if (bytes.Length < RecordVersionStamp.HeaderSize + 2)
            {
                throw new DocumentCatalogException("Truncated document catalog record.");
            }
            var (writer, deleter) = RecordVersionStamp.ReadStamps(bytes.Span);
            if (writer != reference.Writer)
            {
                versions.RemoveAt(i);
                continue;
            }
            var record = DocumentCatalogCodec.Decode(bytes.Span);
            if (!matches(record))
            {
                versions.RemoveAt(i);
                continue;
            }
            if (snapshot.IsVisible(writer) && (deleter == TransactionSequence.None || !snapshot.IsVisible(deleter))
                && (result is null || writer > result.Value.Reference.Writer))
            {
                result = new Found(reference, record);
            }
        }
        return result;
    }

    private static void EnsureActive(ITransactionContext context)
    {
        if (context.State != TransactionState.Active)
        {
            throw new InvalidOperationException("Catalog mutations require an active transaction.");
        }
    }

    private readonly record struct Reference(PageId PageId, int SlotIndex, TransactionSequence Writer);
    private readonly record struct Found(Reference Reference, CatalogRecord Record);
}

internal readonly record struct CatalogRecord(DocumentCollectionMetadata? Collection, DocumentCatalogEntry? Document, DocumentIndexMetadata? Index = null);


