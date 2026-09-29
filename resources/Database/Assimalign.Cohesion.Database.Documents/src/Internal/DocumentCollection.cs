using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Assimalign.Cohesion.Database.Documents.Catalog;
using Assimalign.Cohesion.Database.Transactions;

namespace Assimalign.Cohesion.Database.Documents.Internal;

internal sealed class DocumentCollection : IDocumentCollection
{
    private readonly DocumentDatabaseInstance _database;
    private readonly DocumentCollectionMetadata _collection;
    private readonly DocumentDatabaseSession? _boundSession;

    /// <summary>
    /// Initializes a new instance of the <see cref="DocumentCollection"/> class.
    /// </summary>
    /// <param name="database">The document database instance that owns the collection.</param>
    /// <param name="collection">The catalog metadata of the collection.</param>
    /// <param name="boundSession">The session the collection is bound to, or <see langword="null"/> when any session of the database may use it.</param>
    public DocumentCollection(DocumentDatabaseInstance database, DocumentCollectionMetadata collection, DocumentDatabaseSession? boundSession)
    {
        _database = database;
        _collection = collection;
        _boundSession = boundSession;
    }

    public string Name => _collection.Name;

    public ValueTask<Document?> GetAsync(IDatabaseSession session, DocumentId id, CancellationToken cancellationToken = default)
    {
        ValidateId(id);
        return _database.RunAsync(ValidateSession(session), operation =>
        {
            EnsureCollection(operation.Context);
            var entry = _database.Catalog.FindDocument(_collection.Id, id.Value, operation.Context.Snapshot);
            return new ValueTask<Document?>(entry is null ? null : _database.ReadDocument(entry.Value));
        }, cancellationToken);
    }

    public ValueTask<Document> PutAsync(IDatabaseSession session, DocumentId id, ReadOnlyMemory<byte> content, DocumentVersion? expectedVersion = null, CancellationToken cancellationToken = default)
    {
        ValidateId(id);
        // Capture caller memory before any await; subsequent mutations cannot alter stored bytes.
        var bytes = content.ToArray();
        return _database.RunAsync(ValidateSession(session), async operation =>
        {
            await _database.LockWriterAsync(operation.Context, cancellationToken).ConfigureAwait(false);
            EnsureCollection(operation.Context, writing: true);
            var previous = _database.Catalog.FindDocument(_collection.Id, id.Value, operation.Context.Snapshot);
            EnsureCurrent(operation.Context, id, previous, expectedVersion);
            var reference = await _database.DataStorage.WriteContentAsync(_database.Coordinator, operation.Context, bytes, cancellationToken).ConfigureAwait(false);
            if (previous is not null)
            {
                await _database.DataStorage.TombstoneContentAsync(_database.Coordinator, operation.Context, DocumentDatabaseInstance.Content(previous.Value), cancellationToken).ConfigureAwait(false);
            }
            // Use the durable kernel allocator: versions never repeat, even after delete/reinsert or restart.
            ulong version = (ulong)_database.DataStorage.ReserveTransactionSequence();
            var entry = new DocumentCatalogEntry(_collection.Id, id.Value, version, reference.Head, reference.Length, reference.Checksum);
            await _database.Catalog.SaveDocumentAsync(entry, operation.Context, cancellationToken).ConfigureAwait(false);
            return new Document(id, new DocumentVersion(version), bytes);
        }, cancellationToken);
    }

    public ValueTask<bool> DeleteAsync(IDatabaseSession session, DocumentId id, DocumentVersion? expectedVersion = null, CancellationToken cancellationToken = default)
    {
        ValidateId(id);
        return _database.RunAsync(ValidateSession(session), async operation =>
        {
            await _database.LockWriterAsync(operation.Context, cancellationToken).ConfigureAwait(false);
            EnsureCollection(operation.Context, writing: true);
            var previous = _database.Catalog.FindDocument(_collection.Id, id.Value, operation.Context.Snapshot);
            EnsureCurrent(operation.Context, id, previous, expectedVersion);
            if (previous is null) { return false; }
            await _database.DataStorage.TombstoneContentAsync(_database.Coordinator, operation.Context, DocumentDatabaseInstance.Content(previous.Value), cancellationToken).ConfigureAwait(false);
            await _database.Catalog.DeleteDocumentAsync(_collection.Id, id.Value, operation.Context, cancellationToken).ConfigureAwait(false);
            return true;
        }, cancellationToken);
    }

    private DocumentDatabaseSession ValidateSession(IDatabaseSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (session is not DocumentDatabaseSession documentSession || !ReferenceEquals(documentSession.Instance, _database) ||
            (_boundSession is not null && !ReferenceEquals(_boundSession, documentSession)))
        {
            throw new DatabaseException("The document collection and session must belong to the same bound database and session.");
        }
        documentSession.ThrowIfNotOpen();
        return documentSession;
    }

    private static void ValidateId(DocumentId id) => ArgumentException.ThrowIfNullOrWhiteSpace(id.Value);

    private void EnsureCurrent(ITransactionContext context, DocumentId id, DocumentCatalogEntry? previous, DocumentVersion? expectedVersion)
    {
        if (previous != _database.Catalog.FindDocument(_collection.Id, id.Value, _database.LatestSnapshot(context))) { DocumentDatabaseInstance.ThrowConflict(); }
        if (expectedVersion is not null && previous?.Version != expectedVersion.Value.Value)
        {
            throw new DatabaseException($"Document '{id}' does not match expected version '{expectedVersion}'.");
        }
    }

    private void EnsureCollection(ITransactionContext context, bool writing = false)
    {
        var current = _database.Catalog.FindCollection(Name, context.Snapshot);
        if (current?.Id != _collection.Id) { throw new DatabaseException($"Collection '{Name}' is no longer available in this transaction."); }
        if (writing && (current != _database.Catalog.FindCollection(Name, _database.LatestSnapshot(context)) ||
            !_database.Catalog.GetIndexes(_collection.Id, context.Snapshot).SequenceEqual(_database.Catalog.GetIndexes(_collection.Id, _database.LatestSnapshot(context)))))
        {
            DocumentDatabaseInstance.ThrowConflict();
        }
    }
}
