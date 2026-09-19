using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Assimalign.Cohesion.Database.Documents.Catalog;
using Assimalign.Cohesion.Database.Transactions;

namespace Assimalign.Cohesion.Database.Documents.Internal;

internal sealed class DocumentCollection(DocumentDatabaseInstance database, DocumentCollectionMetadata collection, DocumentDatabaseSession? boundSession) : IDocumentCollection
{
    public string Name => collection.Name;

    public ValueTask<Document?> GetAsync(IDatabaseSession session, DocumentId id, CancellationToken cancellationToken = default)
    {
        ValidateId(id);
        return database.RunAsync(ValidateSession(session), operation =>
        {
            EnsureCollection(operation.Context);
            var entry = database.Catalog.FindDocument(collection.Id, id.Value, operation.Context.Snapshot);
            return new ValueTask<Document?>(entry is null ? null : database.ReadDocument(entry.Value));
        }, cancellationToken);
    }

    public ValueTask<Document> PutAsync(IDatabaseSession session, DocumentId id, ReadOnlyMemory<byte> content, DocumentVersion? expectedVersion = null, CancellationToken cancellationToken = default)
    {
        ValidateId(id);
        // Capture caller memory before any await; subsequent mutations cannot alter stored bytes.
        var bytes = content.ToArray();
        return database.RunAsync(ValidateSession(session), async operation =>
        {
            await database.LockWriterAsync(operation.Context, cancellationToken).ConfigureAwait(false);
            EnsureCollection(operation.Context, writing: true);
            var previous = database.Catalog.FindDocument(collection.Id, id.Value, operation.Context.Snapshot);
            EnsureCurrent(operation.Context, id, previous, expectedVersion);
            var reference = await database.DataStorage.WriteContentAsync(database.Coordinator, operation.Context, bytes, cancellationToken).ConfigureAwait(false);
            if (previous is not null)
            {
                await database.DataStorage.TombstoneContentAsync(database.Coordinator, operation.Context, DocumentDatabaseInstance.Content(previous.Value), cancellationToken).ConfigureAwait(false);
            }
            // Use the durable kernel allocator: versions never repeat, even after delete/reinsert or restart.
            ulong version = (ulong)database.DataStorage.ReserveTransactionSequence();
            var entry = new DocumentCatalogEntry(collection.Id, id.Value, version, reference.Head, reference.Length, reference.Checksum);
            await database.Catalog.SaveDocumentAsync(entry, operation.Context, cancellationToken).ConfigureAwait(false);
            return new Document(id, new DocumentVersion(version), bytes);
        }, cancellationToken);
    }

    public ValueTask<bool> DeleteAsync(IDatabaseSession session, DocumentId id, DocumentVersion? expectedVersion = null, CancellationToken cancellationToken = default)
    {
        ValidateId(id);
        return database.RunAsync(ValidateSession(session), async operation =>
        {
            await database.LockWriterAsync(operation.Context, cancellationToken).ConfigureAwait(false);
            EnsureCollection(operation.Context, writing: true);
            var previous = database.Catalog.FindDocument(collection.Id, id.Value, operation.Context.Snapshot);
            EnsureCurrent(operation.Context, id, previous, expectedVersion);
            if (previous is null) { return false; }
            await database.DataStorage.TombstoneContentAsync(database.Coordinator, operation.Context, DocumentDatabaseInstance.Content(previous.Value), cancellationToken).ConfigureAwait(false);
            await database.Catalog.DeleteDocumentAsync(collection.Id, id.Value, operation.Context, cancellationToken).ConfigureAwait(false);
            return true;
        }, cancellationToken);
    }

    private DocumentDatabaseSession ValidateSession(IDatabaseSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (session is not DocumentDatabaseSession documentSession || !ReferenceEquals(documentSession.Instance, database) ||
            (boundSession is not null && !ReferenceEquals(boundSession, documentSession)))
        {
            throw new DatabaseException("The document collection and session must belong to the same bound database and session.");
        }
        documentSession.ThrowIfNotOpen();
        return documentSession;
    }

    private static void ValidateId(DocumentId id) => ArgumentException.ThrowIfNullOrWhiteSpace(id.Value);

    private void EnsureCurrent(ITransactionContext context, DocumentId id, DocumentCatalogEntry? previous, DocumentVersion? expectedVersion)
    {
        if (previous != database.Catalog.FindDocument(collection.Id, id.Value, database.LatestSnapshot(context))) { DocumentDatabaseInstance.ThrowConflict(); }
        if (expectedVersion is not null && previous?.Version != expectedVersion.Value.Value)
        {
            throw new DatabaseException($"Document '{id}' does not match expected version '{expectedVersion}'.");
        }
    }

    private void EnsureCollection(ITransactionContext context, bool writing = false)
    {
        var current = database.Catalog.FindCollection(Name, context.Snapshot);
        if (current?.Id != collection.Id) { throw new DatabaseException($"Collection '{Name}' is no longer available in this transaction."); }
        if (writing && (current != database.Catalog.FindCollection(Name, database.LatestSnapshot(context)) ||
            !database.Catalog.GetIndexes(collection.Id, context.Snapshot).SequenceEqual(database.Catalog.GetIndexes(collection.Id, database.LatestSnapshot(context)))))
        {
            DocumentDatabaseInstance.ThrowConflict();
        }
    }
}
