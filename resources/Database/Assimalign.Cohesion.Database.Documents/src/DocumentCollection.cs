using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Database.Documents.Catalog;
using Assimalign.Cohesion.Database.Transactions;

namespace Assimalign.Cohesion.Database.Documents;

/// <summary>
/// A named collection of versioned documents.
/// </summary>
/// <remarks>
/// <para>
/// Operations take the session they execute in so document reads and writes share the session's
/// transaction and MVCC snapshot: each runs as one statement of that session. Optimistic
/// concurrency uses <see cref="DocumentVersion"/>: pass the expected version to make a write
/// conditional; a mismatch fails with a <see cref="DatabaseException"/>.
/// </para>
/// <para>
/// A collection a session returned (<see cref="DocumentDatabaseSession.CreateCollectionAsync"/> and
/// its siblings) is bound to that session and refuses any other; a collection the database
/// returned (<see cref="DocumentDatabase.CreateCollectionAsync"/> and its siblings) takes any
/// session of its database.
/// </para>
/// <para>
/// <b>Shape (concrete-types plan, phase 4, #1260).</b> A public sealed type with an internal
/// constructor, replacing the former <c>IDocumentCollection</c> interface and its internal
/// implementation; its operations take the typed <see cref="DocumentDatabaseSession"/>.
/// </para>
/// </remarks>
public sealed class DocumentCollection
{
    private readonly DocumentDatabase _database;
    private readonly DocumentCollectionMetadata _collection;
    private readonly DocumentDatabaseSession? _boundSession;

    /// <summary>
    /// Initializes a new instance of the <see cref="DocumentCollection"/> class.
    /// </summary>
    /// <param name="database">The document database that owns the collection.</param>
    /// <param name="collection">The catalog metadata of the collection.</param>
    /// <param name="boundSession">The session the collection is bound to, or <see langword="null"/> when any session of the database may use it.</param>
    internal DocumentCollection(DocumentDatabase database, DocumentCollectionMetadata collection, DocumentDatabaseSession? boundSession)
    {
        _database = database;
        _collection = collection;
        _boundSession = boundSession;
    }

    /// <summary>
    /// Gets the name of the collection, unique within its database.
    /// </summary>
    public string Name => _collection.Name;

    /// <summary>
    /// Reads a document by identity.
    /// </summary>
    /// <param name="session">The session the read executes in.</param>
    /// <param name="id">The document identity.</param>
    /// <param name="cancellationToken">Cancellation token for the operation.</param>
    /// <returns>The document, or null when no visible document has the identity.</returns>
    /// <exception cref="ArgumentException"><paramref name="id"/> is empty or white space.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="session"/> is null.</exception>
    /// <exception cref="ObjectDisposedException">The database has been disposed.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was canceled.</exception>
    /// <exception cref="DatabaseException">
    /// The session belongs to another database, or the collection is bound to another session; the
    /// session is closed, another statement holds it, or its transaction refuses statements
    /// (<c>COHDBD001</c>); or the collection is no longer available in the statement's snapshot.
    /// </exception>
    /// <exception cref="DatabaseOfflineException">The database is offline (<c>COHDBD002</c>, #1243).</exception>
    /// <exception cref="DatabaseTransactionCommitUnconfirmedException">An autocommit statement's commit record could not be confirmed durable.</exception>
    public ValueTask<Document?> GetAsync(DocumentDatabaseSession session, DocumentId id, CancellationToken cancellationToken = default)
    {
        ValidateId(id);
        return _database.RunAsync(ValidateSession(session), operation =>
        {
            EnsureCollection(operation.Context);
            var entry = _database.Catalog.FindDocument(_collection.Id, id.Value, operation.Context.Snapshot);
            return new ValueTask<Document?>(entry is null ? null : _database.ReadDocument(entry.Value));
        }, cancellationToken);
    }

    /// <summary>
    /// Writes a document, inserting or replacing by identity.
    /// </summary>
    /// <param name="session">The session the write executes in.</param>
    /// <param name="id">The document identity.</param>
    /// <param name="content">The document content as UTF-8 JSON.</param>
    /// <param name="expectedVersion">When set, the write succeeds only if the stored version matches.</param>
    /// <param name="cancellationToken">Cancellation token for the operation.</param>
    /// <returns>The stored document with its new version.</returns>
    /// <exception cref="ArgumentException"><paramref name="id"/> is empty or white space.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="session"/> is null.</exception>
    /// <exception cref="ObjectDisposedException">The database has been disposed.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was canceled.</exception>
    /// <exception cref="DatabaseException">
    /// <paramref name="expectedVersion"/> is set and does not match; the session belongs to another
    /// database, or the collection is bound to another session; the session is closed, another
    /// statement holds it, or its transaction refuses statements (<c>COHDBD001</c>); or the
    /// collection is no longer available in the statement's snapshot.
    /// </exception>
    /// <exception cref="DatabaseOfflineException">The database is offline (<c>COHDBD002</c>, #1243).</exception>
    /// <exception cref="DatabaseTransactionAbortedException">The document, the collection or its indexes changed since the transaction's snapshot (retryable).</exception>
    /// <exception cref="DatabaseTransactionCommitUnconfirmedException">An autocommit statement's commit record could not be confirmed durable.</exception>
    /// <exception cref="DocumentCatalogException">An indexed field of the document holds a scalar that encodes beyond the 1024-byte index key limit.</exception>
    public ValueTask<Document> PutAsync(DocumentDatabaseSession session, DocumentId id, ReadOnlyMemory<byte> content, DocumentVersion? expectedVersion = null, CancellationToken cancellationToken = default)
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
                await _database.DataStorage.TombstoneContentAsync(_database.Coordinator, operation.Context, DocumentDatabase.Content(previous.Value), cancellationToken).ConfigureAwait(false);
            }
            // Use the durable kernel allocator: versions never repeat, even after delete/reinsert or restart.
            ulong version = (ulong)_database.DataStorage.ReserveTransactionSequence();
            var entry = new DocumentCatalogEntry(_collection.Id, id.Value, version, reference.Head, reference.Length, reference.Checksum);
            await _database.Catalog.SaveDocumentAsync(entry, operation.Context, cancellationToken).ConfigureAwait(false);
            return new Document(id, new DocumentVersion(version), bytes);
        }, cancellationToken);
    }

    /// <summary>
    /// Deletes a document by identity.
    /// </summary>
    /// <param name="session">The session the delete executes in.</param>
    /// <param name="id">The document identity.</param>
    /// <param name="expectedVersion">When set, the delete succeeds only if the stored version matches.</param>
    /// <param name="cancellationToken">Cancellation token for the operation.</param>
    /// <returns>True when a document was deleted; false when none was visible.</returns>
    /// <exception cref="ArgumentException"><paramref name="id"/> is empty or white space.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="session"/> is null.</exception>
    /// <exception cref="ObjectDisposedException">The database has been disposed.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was canceled.</exception>
    /// <exception cref="DatabaseException">
    /// <paramref name="expectedVersion"/> is set and does not match; the session belongs to another
    /// database, or the collection is bound to another session; the session is closed, another
    /// statement holds it, or its transaction refuses statements (<c>COHDBD001</c>); or the
    /// collection is no longer available in the statement's snapshot.
    /// </exception>
    /// <exception cref="DatabaseOfflineException">The database is offline (<c>COHDBD002</c>, #1243).</exception>
    /// <exception cref="DatabaseTransactionAbortedException">The document, the collection or its indexes changed since the transaction's snapshot (retryable).</exception>
    /// <exception cref="DatabaseTransactionCommitUnconfirmedException">An autocommit statement's commit record could not be confirmed durable.</exception>
    public ValueTask<bool> DeleteAsync(DocumentDatabaseSession session, DocumentId id, DocumentVersion? expectedVersion = null, CancellationToken cancellationToken = default)
    {
        ValidateId(id);
        return _database.RunAsync(ValidateSession(session), async operation =>
        {
            await _database.LockWriterAsync(operation.Context, cancellationToken).ConfigureAwait(false);
            EnsureCollection(operation.Context, writing: true);
            var previous = _database.Catalog.FindDocument(_collection.Id, id.Value, operation.Context.Snapshot);
            EnsureCurrent(operation.Context, id, previous, expectedVersion);
            if (previous is null) { return false; }
            await _database.DataStorage.TombstoneContentAsync(_database.Coordinator, operation.Context, DocumentDatabase.Content(previous.Value), cancellationToken).ConfigureAwait(false);
            await _database.Catalog.DeleteDocumentAsync(_collection.Id, id.Value, operation.Context, cancellationToken).ConfigureAwait(false);
            return true;
        }, cancellationToken);
    }

    private DocumentDatabaseSession ValidateSession(DocumentDatabaseSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (!ReferenceEquals(session.Database, _database) || (_boundSession is not null && !ReferenceEquals(_boundSession, session)))
        {
            throw new DatabaseException("The document collection and session must belong to the same bound database and session.");
        }
        session.ThrowIfNotOpen();
        return session;
    }

    private static void ValidateId(DocumentId id) => ArgumentException.ThrowIfNullOrWhiteSpace(id.Value);

    private void EnsureCurrent(TransactionContext context, DocumentId id, DocumentCatalogEntry? previous, DocumentVersion? expectedVersion)
    {
        if (previous != _database.Catalog.FindDocument(_collection.Id, id.Value, _database.LatestSnapshot(context))) { DocumentDatabase.ThrowConflict(); }
        if (expectedVersion is not null && previous?.Version != expectedVersion.Value.Value)
        {
            throw new DatabaseException($"Document '{id}' does not match expected version '{expectedVersion}'.");
        }
    }

    private void EnsureCollection(TransactionContext context, bool writing = false)
    {
        var current = _database.Catalog.FindCollection(Name, context.Snapshot);
        if (current?.Id != _collection.Id) { throw new DatabaseException($"Collection '{Name}' is no longer available in this transaction."); }
        if (writing && (current != _database.Catalog.FindCollection(Name, _database.LatestSnapshot(context)) ||
            !_database.Catalog.GetIndexes(_collection.Id, context.Snapshot).SequenceEqual(_database.Catalog.GetIndexes(_collection.Id, _database.LatestSnapshot(context)))))
        {
            DocumentDatabase.ThrowConflict();
        }
    }
}
