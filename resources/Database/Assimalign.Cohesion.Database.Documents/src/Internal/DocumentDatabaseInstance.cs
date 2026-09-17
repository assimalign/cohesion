using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Assimalign.Cohesion.Database.Documents.Catalog;
using Assimalign.Cohesion.Database.Documents.Storage;
using Assimalign.Cohesion.Database.Transactions;

namespace Assimalign.Cohesion.Database.Documents.Internal;

internal sealed class DocumentDatabaseInstance : IDocumentDatabase
{
    private int _disposed;
    internal DocumentDatabaseInstance(string name, IDatabaseEngine engine, DocumentStorage storage, bool recover)
    {
        Name = name;
        Engine = engine;
        DataStorage = storage;
        Coordinator = new TransactionCoordinator(storage, storage.WriteAheadJournal, storage.Records);
        if (recover)
        {
            var recovery = Coordinator.AnalyzeAndScrub();
            Catalog = DocumentCatalog.Open(storage, Coordinator);
            Catalog.RecoverIndexesAsync(recovery.Aborted).AsTask().GetAwaiter().GetResult();
            Coordinator.CompleteRecovery();
        }
        else { Catalog = DocumentCatalog.Open(storage, Coordinator); }
    }

    public DatabaseName Name { get; }
    public IDatabaseEngine Engine { get; }
    internal DocumentStorage DataStorage { get; }
    internal TransactionCoordinator Coordinator { get; }
    internal IDocumentCatalog Catalog { get; }

    public ValueTask<IDatabaseSession> CreateSessionAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        cancellationToken.ThrowIfCancellationRequested();
        return new ValueTask<IDatabaseSession>(new DocumentDatabaseSession(this));
    }

    public ValueTask<IDocumentCollection> CreateCollectionAsync(string name, CancellationToken cancellationToken = default)
        => CreateCollectionAsync(name, null, cancellationToken);
    internal ValueTask<IDocumentCollection> CreateCollectionAsync(string name, DocumentDatabaseSession? session, CancellationToken token)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return RunAsync(session, async operation =>
        {
            await LockWriterAsync(operation.Context, token).ConfigureAwait(false);
            var previous = Catalog.FindCollection(name, operation.Context.Snapshot);
            if (previous != Catalog.FindCollection(name, LatestSnapshot(operation.Context)))
            {
                ThrowConflict();
            }

            if (previous is not null)
            {
                throw new DatabaseException($"Collection '{name}' already exists.");
            }

            var metadata = new DocumentCollectionMetadata(Guid.NewGuid(), name);
            await Catalog.SaveCollectionAsync(metadata, operation.Context, token).ConfigureAwait(false);
            return (IDocumentCollection)new DocumentCollection(this, metadata, session);
        }, token);
    }

    public ValueTask<IDocumentCollection> GetCollectionAsync(string name, CancellationToken cancellationToken = default)
        => GetCollectionAsync(name, null, cancellationToken);
    internal ValueTask<IDocumentCollection> GetCollectionAsync(string name, DocumentDatabaseSession? session, CancellationToken token)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return RunAsync(session, operation => new ValueTask<IDocumentCollection>(new DocumentCollection(this,
            Catalog.FindCollection(name, operation.Context.Snapshot) ?? throw new DatabaseException($"Collection '{name}' does not exist."), session)), token);
    }

    public ValueTask DropCollectionAsync(string name, CancellationToken cancellationToken = default)
        => DropCollectionAsync(name, null, cancellationToken);
    internal async ValueTask DropCollectionAsync(string name, DocumentDatabaseSession? session, CancellationToken token)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        await RunAsync(session, async operation =>
        {
            await LockWriterAsync(operation.Context, token).ConfigureAwait(false);
            var collection = Catalog.FindCollection(name, operation.Context.Snapshot)
                ?? throw new DatabaseException($"Collection '{name}' does not exist.");
            if (collection != Catalog.FindCollection(name, LatestSnapshot(operation.Context)))
            {
                ThrowConflict();
            }
            // Same authority rule as SqlPlanExecutor.EnsureCanChange. Document has
            // no provisioning authority, so every schema-owned drop is locked.
            if (collection.Owner == DatabaseObjectOwner.Schema)
            {
                throw new DatabaseObjectLockedException(collection.Name, collection.OwningSchema!, "DROP COLLECTION");
            }

            var visible = Catalog.GetDocuments(collection.Id, null, operation.Context.Snapshot);
            if (!visible.SequenceEqual(Catalog.GetDocuments(collection.Id, null, LatestSnapshot(operation.Context))) ||
                !Catalog.GetIndexes(collection.Id, operation.Context.Snapshot)
                    .SequenceEqual(Catalog.GetIndexes(collection.Id, LatestSnapshot(operation.Context))))
            {
                ThrowConflict();
            }

            foreach (var document in visible)
            {
                await DataStorage.TombstoneContentAsync(Coordinator, operation.Context, Content(document), token).ConfigureAwait(false);
                await Catalog.DeleteDocumentAsync(collection.Id, document.Id, operation.Context, token).ConfigureAwait(false);
            }
            foreach (var index in Catalog.GetIndexes(collection.Id, operation.Context.Snapshot))
            {
                await Catalog.DeleteIndexAsync(collection.Id, index.Name, operation.Context, token).ConfigureAwait(false);
            }
            await Catalog.DeleteCollectionAsync(collection.Id, operation.Context, token).ConfigureAwait(false);
            return true;
        }, token).ConfigureAwait(false);
    }

    public IAsyncEnumerable<IDocumentCollection> GetCollectionsAsync(CancellationToken cancellationToken = default)
        => GetCollectionsAsync(null, cancellationToken);
    internal async IAsyncEnumerable<IDocumentCollection> GetCollectionsAsync(DocumentDatabaseSession? session, [EnumeratorCancellation] CancellationToken token)
    {
        var collections = await RunAsync(session, operation => new ValueTask<IReadOnlyList<DocumentCollectionMetadata>>(
            Catalog.GetCollections(operation.Context.Snapshot)), token).ConfigureAwait(false);
        foreach (var metadata in collections)
        {
            token.ThrowIfCancellationRequested();
            ThrowIfDisposed();
            session?.ThrowIfNotOpen();
            yield return new DocumentCollection(this, metadata, session);
        }
    }

    internal async ValueTask<DocumentOperation> BeginOperationAsync(DocumentDatabaseSession? session, CancellationToken token)
    {
        ThrowIfDisposed();
        token.ThrowIfCancellationRequested();
        var explicitTransaction = session?.ReserveOperation();
        DocumentOperation? operation = null;
        try
        {
            var context = explicitTransaction?.Context ?? await Coordinator.BeginAsync(IsolationLevel.Snapshot, token).ConfigureAwait(false);
            operation = new DocumentOperation(this, session, context, explicitTransaction);
            await operation.InitializeAsync(token).ConfigureAwait(false);
            session?.Track(operation);
            return operation;
        }
        catch
        {
            if (operation is not null)
            {
                await operation.AbortAsync().ConfigureAwait(false);
            }
            throw;
        }
        finally
        {
            session?.ReleaseReservation();
        }
    }

    internal async ValueTask<T> RunAsync<T>(DocumentDatabaseSession? session, Func<DocumentOperation, ValueTask<T>> action, CancellationToken token)
    {
        var operation = await BeginOperationAsync(session, token).ConfigureAwait(false);
        try
        {
            var result = await action(operation).ConfigureAwait(false);
            await operation.CompleteAsync().ConfigureAwait(false);
            return result;
        }
        catch
        {
            await operation.AbortAsync().ConfigureAwait(false);
            throw;
        }
    }

    // One database writer at a time is deliberately conservative. The shared
    // lock manager owns waits and releases; readers remain snapshot based.
    internal async ValueTask LockWriterAsync(ITransactionContext context, CancellationToken token)
    {
        await Coordinator.LockManager.AcquireAsync(context.Sequence, LockResource.Database(), LockMode.Exclusive, token).ConfigureAwait(false);
        try
        {
            // A session may close or its transaction may roll back while this
            // request waits. ReleaseAll at rollback removes grants, not pending
            // requests, so a subsequently granted inactive owner must release
            // its new grant before the operation leaves the wait.
            token.ThrowIfCancellationRequested();
            ThrowIfDisposed();
            if (context.State != TransactionState.Active)
            {
                throw new DatabaseException("The document operation's transaction ended while waiting for the writer lock.");
            }
        }
        catch
        {
            Coordinator.LockManager.ReleaseAll(context.Sequence);
            throw;
        }
    }

    // Called only under the database writer lock, after all earlier writers
    // finished. Preserve the caller's own uncommitted writes in the latest view.
    internal TransactionSnapshot LatestSnapshot(ITransactionContext context) => new(context.Sequence,
        TransactionSequence.None, new TransactionSequence(ulong.MaxValue), Coordinator.GetOpenContexts().Select(item => item.Sequence));

    internal static void ThrowConflict() => throw new DatabaseTransactionAbortedException("The document catalog changed since this transaction's snapshot. Retry the transaction.");
    internal static DocumentContentReference Content(DocumentCatalogEntry entry) => new(entry.HeadLocation, entry.Length, entry.Checksum);
    internal Document ReadDocument(DocumentCatalogEntry entry)
        => new(new DocumentId(entry.Id), new DocumentVersion(entry.Version), DataStorage.ReadContent(Content(entry)));

    internal async ValueTask ChangeIndexAsync(string collectionName, string indexName, string? path, DocumentDatabaseSession? session, CancellationToken token)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(collectionName);
        ArgumentException.ThrowIfNullOrWhiteSpace(indexName);
        await RunAsync(session, async operation =>
        {
            await LockWriterAsync(operation.Context, token).ConfigureAwait(false);
            var collection = Catalog.FindCollection(collectionName, operation.Context.Snapshot)
                ?? throw new DatabaseException($"Collection '{collectionName}' does not exist.");
            if (collection != Catalog.FindCollection(collectionName, LatestSnapshot(operation.Context))) { ThrowConflict(); }
            if (collection.Owner == DatabaseObjectOwner.Schema)
            {
                throw new DatabaseObjectLockedException(collection.Name, collection.OwningSchema!, "ALTER COLLECTION");
            }
            if (!Catalog.GetDocuments(collection.Id, null, operation.Context.Snapshot)
                .SequenceEqual(Catalog.GetDocuments(collection.Id, null, LatestSnapshot(operation.Context))) ||
                !Catalog.GetIndexes(collection.Id, operation.Context.Snapshot)
                .SequenceEqual(Catalog.GetIndexes(collection.Id, LatestSnapshot(operation.Context)))) { ThrowConflict(); }
            if (path is null) { await Catalog.DeleteIndexAsync(collection.Id, indexName, operation.Context, token).ConfigureAwait(false); }
            else { await Catalog.CreateIndexAsync(collection.Id, indexName, path, operation.Context, token).ConfigureAwait(false); }
            return true;
        }, token).ConfigureAwait(false);
    }
    internal void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        try
        {
            Coordinator.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
        finally
        {
            DataStorage.Dispose();
        }
    }
    public ValueTask DisposeAsync() { Dispose(); return default; }
}
