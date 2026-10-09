using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Database.Documents.Catalog.Internal;
using Assimalign.Cohesion.Database.Documents.Storage;
using Assimalign.Cohesion.Database.Indexing;
using Assimalign.Cohesion.Database.Storage;
using Assimalign.Cohesion.Database.Transactions;

namespace Assimalign.Cohesion.Database.Documents.Catalog;

/// <summary>The snapshot-visible collection and document metadata of one logical database.</summary>
/// <remarks>
/// <para>
/// Mutations join the supplied logical transaction and do not commit it. The caller
/// acquires model locks and resolves write conflicts before invoking a mutation.
/// Listing uses the metadata directory and never traverses document content.
/// </para>
/// <para>
/// <b>Shape (concrete-types plan, phase 4, #1260).</b> One sealed type with a private
/// constructor behind <see cref="Open"/>: it replaces the former <c>IDocumentCatalog</c>
/// interface, the <c>DocumentCatalog</c> static factory and their internal implementation (the
/// triplet of <c>database-area.md</c> rule 1).
/// </para>
/// </remarks>
public sealed partial class DocumentCatalog
{
    private readonly DocumentStorage _storage;
    private readonly TransactionCoordinator _coordinator;
    private readonly object _sync = new();
    private readonly Dictionary<string, List<Reference>> _collections = new(StringComparer.Ordinal);
    private readonly Dictionary<(Guid CollectionId, string Name), List<Reference>> _documents = new();

    private DocumentCatalog(DocumentStorage storage, TransactionCoordinator coordinator)
    {
        _storage = storage;
        _coordinator = coordinator;
    }

    /// <summary>Loads the metadata directory after the coordinator's recovery scrub.</summary>
    /// <param name="storage">The same storage that holds chunk records.</param>
    /// <param name="coordinator">The coordinator bound to that storage and its record space.</param>
    /// <returns>The database's metadata catalog.</returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <exception cref="DocumentCatalogException">Persisted catalog metadata is malformed or unsupported.</exception>
    /// <exception cref="IndexFormatException">An index tree is in another B-tree page format.</exception>
    public static DocumentCatalog Open(DocumentStorage storage, TransactionCoordinator coordinator)
    {
        ArgumentNullException.ThrowIfNull(storage);
        ArgumentNullException.ThrowIfNull(coordinator);
        var catalog = new DocumentCatalog(storage, coordinator);
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

    /// <summary>
    /// Checks that every index tree the storage registers is in the B-tree page format
    /// this engine reads (<see cref="BTreeIndexManager.FormatVersion"/>), reading only
    /// the registrations and each tree's root page. An engine calls it before the
    /// coordinator's recovery scrub, so a database whose indexes it cannot read is
    /// refused before anything is written to it; <see cref="Open"/> checks again as it
    /// attaches the trees. Registration records carry no MVCC stamps, so they read the
    /// same before and after the scrub.
    /// </summary>
    /// <param name="storage">The storage that holds the index registrations and trees.</param>
    /// <exception cref="ArgumentNullException"><paramref name="storage"/> is null.</exception>
    /// <exception cref="IndexFormatException">An index tree is in another B-tree page format.</exception>
    /// <exception cref="DocumentCatalogException">A persisted index registration is malformed.</exception>
    public static void EnsureIndexFormat(DocumentStorage storage)
    {
        ArgumentNullException.ThrowIfNull(storage);
        BTreeIndexManager.EnsureFormat(storage, ReadRegistrations(storage).Select(entry => entry.Registration));
    }

    /// <summary>Finds a collection visible through a snapshot.</summary>
    /// <param name="name">The case-sensitive collection name.</param>
    /// <param name="snapshot">The reader's visibility snapshot.</param>
    /// <returns>The visible collection, or null.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="name"/> or <paramref name="snapshot"/> is null.</exception>
    /// <exception cref="DocumentCatalogException">A persisted catalog record is malformed.</exception>
    public DocumentCollectionMetadata? FindCollection(string name, TransactionSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(snapshot);
        lock (_sync)
        {
            return FindCollectionVersion(name, snapshot)?.Record.Collection;
        }
    }

    /// <summary>Lists collections visible through a snapshot, ordered by ordinal name.</summary>
    /// <param name="snapshot">The reader's visibility snapshot.</param>
    /// <returns>The visible collection metadata.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="snapshot"/> is null.</exception>
    /// <exception cref="DocumentCatalogException">A persisted catalog record is malformed.</exception>
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

    /// <summary>Creates or replaces a collection metadata version in the supplied transaction.</summary>
    /// <param name="collection">The complete collection metadata.</param>
    /// <param name="context">The logical transaction owning the mutation.</param>
    /// <param name="cancellationToken">Cancels the operation before physical application.</param>
    /// <returns>A task representing the operation.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="context"/> is null.</exception>
    /// <exception cref="ArgumentException">
    /// The collection identity or name is invalid, or a schema-owned collection names no owning schema.
    /// </exception>
    /// <exception cref="InvalidOperationException"><paramref name="context"/> is not active.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was canceled before the physical application.</exception>
    /// <exception cref="TransactionAbortedException">The transaction ended, or its end began, before the mutation was applied.</exception>
    public async ValueTask SaveCollectionAsync(DocumentCollectionMetadata collection, TransactionContext context, CancellationToken cancellationToken = default)
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

    /// <summary>Tombstones a collection metadata version in the supplied transaction.</summary>
    /// <param name="collectionId">The stable collection identity.</param>
    /// <param name="context">The logical transaction owning the mutation.</param>
    /// <param name="cancellationToken">Cancels the operation before physical application.</param>
    /// <returns>A task representing the operation.</returns>
    /// <remarks>The caller handles ownership enforcement and deletion of contained documents.</remarks>
    /// <exception cref="ArgumentNullException"><paramref name="context"/> is null.</exception>
    /// <exception cref="InvalidOperationException"><paramref name="context"/> is not active.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was canceled before the physical application.</exception>
    /// <exception cref="TransactionAbortedException">The transaction ended, or its end began, before the mutation was applied.</exception>
    public async ValueTask DeleteCollectionAsync(Guid collectionId, TransactionContext context, CancellationToken cancellationToken = default)
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

    /// <summary>Finds a document metadata version visible through a snapshot.</summary>
    /// <param name="collectionId">The stable collection identity.</param>
    /// <param name="name">The case-sensitive document name.</param>
    /// <param name="snapshot">The reader's visibility snapshot.</param>
    /// <returns>The visible document metadata, or null.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="name"/> or <paramref name="snapshot"/> is null.</exception>
    /// <exception cref="DocumentCatalogException">A persisted catalog record is malformed.</exception>
    public DocumentCatalogEntry? FindDocument(Guid collectionId, string name, TransactionSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(snapshot);
        lock (_sync)
        {
            return FindDocumentVersion(collectionId, name, snapshot)?.Record.Document;
        }
    }

    /// <summary>Lists visible document metadata, optionally filtered by an ordinal name prefix.</summary>
    /// <param name="collectionId">The stable collection identity.</param>
    /// <param name="prefix">The case-sensitive name prefix, or null for all names.</param>
    /// <param name="snapshot">The reader's visibility snapshot.</param>
    /// <returns>The visible document metadata ordered by ordinal name.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="snapshot"/> is null.</exception>
    /// <exception cref="DocumentCatalogException">A persisted catalog record is malformed.</exception>
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

    /// <summary>Creates or replaces document metadata in the supplied transaction.</summary>
    /// <param name="document">The complete document metadata and chunk head reference.</param>
    /// <param name="context">The logical transaction owning the mutation.</param>
    /// <param name="cancellationToken">Cancels the operation before physical application.</param>
    /// <returns>A task representing the operation.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="context"/> is null.</exception>
    /// <exception cref="ArgumentException">The document identity, name, or length is invalid.</exception>
    /// <exception cref="InvalidOperationException"><paramref name="context"/> is not active.</exception>
    /// <exception cref="DocumentCatalogException">An indexed scalar of the document encodes beyond the index key limit.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was canceled before the physical application.</exception>
    /// <exception cref="TransactionAbortedException">The transaction ended, or its end began, before the mutation was applied.</exception>
    public async ValueTask SaveDocumentAsync(DocumentCatalogEntry document, TransactionContext context, CancellationToken cancellationToken = default)
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

    /// <summary>Tombstones document metadata in the supplied transaction.</summary>
    /// <param name="collectionId">The stable collection identity.</param>
    /// <param name="name">The case-sensitive document name.</param>
    /// <param name="context">The logical transaction owning the mutation.</param>
    /// <param name="cancellationToken">Cancels the operation before physical application.</param>
    /// <returns>A task representing the operation.</returns>
    /// <remarks>The caller tombstones the content chain in the same logical transaction.</remarks>
    /// <exception cref="ArgumentNullException"><paramref name="name"/> or <paramref name="context"/> is null.</exception>
    /// <exception cref="InvalidOperationException"><paramref name="context"/> is not active.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was canceled before the physical application.</exception>
    /// <exception cref="TransactionAbortedException">The transaction ended, or its end began, before the mutation was applied.</exception>
    public async ValueTask DeleteDocumentAsync(Guid collectionId, string name, TransactionContext context, CancellationToken cancellationToken = default)
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

    private async ValueTask SaveAsync(CatalogRecord record, Found? previous, TransactionContext context, CancellationToken cancellationToken)
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

    private async ValueTask DeleteAsync(Found? previous, TransactionContext context, CancellationToken cancellationToken)
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

    private void Tombstone(StorageTransaction bracket, Reference reference, TransactionSequence writer)
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
            // Only a reclaimed location (a deleted or reverted slot, an owner-zero page the
            // purge freed or the allocator reused) and a changed identity invalidate a cached
            // physical reference. CRC and I/O exceptions deliberately propagate (#1342).
            if (!_storage.TryReadRecord(reference.PageId, reference.SlotIndex, 0, out var bytes))
            {
                versions.RemoveAt(i);
                continue;
            }
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

    private static void EnsureActive(TransactionContext context)
    {
        if (context.State != TransactionState.Active)
        {
            throw new InvalidOperationException("Catalog mutations require an active transaction.");
        }
    }

    private readonly record struct Reference(PageId PageId, int SlotIndex, TransactionSequence Writer);
    private readonly record struct Found(Reference Reference, CatalogRecord Record);
}
