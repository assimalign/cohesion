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

        if (engine is DocumentDatabaseEngine owner)
        {
            // A deferred undo is retried on its own backoff, from about 100 ms up to the
            // maintenance interval, and the purge worker wakes for it (#1226).
            Coordinator.DeferredUndoRetryLimit = owner.EngineOptions.MaintenanceInterval;
            Coordinator.DeferredUndoRetryDelay = owner.EngineOptions.DeferredUndoRetryDelay;
            Coordinator.OnUndoDeferred = owner.UndoDeferredSignal.Set;
        }

        // Indexing owns the B-tree page format (#1194) and checks each tree's root
        // page as it attaches the tree. That happens inside the catalog's open, after
        // the recovery scrub has written to the database, so the check runs here
        // first: a database whose indexes this engine cannot read is refused before
        // anything is written to it.
        try
        {
            DocumentCatalog.EnsureIndexFormat(storage);
        }
        catch (Indexing.IndexFormatException exception)
        {
            Coordinator.DisposeAsync().AsTask().GetAwaiter().GetResult();
            throw new DatabaseException($"Database '{name}' cannot be opened. {exception.Message}", exception);
        }

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
        ThrowIfOffline();
        cancellationToken.ThrowIfCancellationRequested();
        return new ValueTask<IDatabaseSession>(new DocumentDatabaseSession(this));
    }

    /// <summary>
    /// The code that leads the message of every operation refused because the database is
    /// offline (#1243).
    /// </summary>
    internal const string OfflineCode = "COHDBD002";

    /// <summary>
    /// Gets whether a failed durable flush took the database offline.
    /// </summary>
    internal bool IsOffline => DataStorage.IsOffline;

    /// <summary>
    /// Refuses an operation on an offline database with <see cref="DatabaseOfflineException"/>
    /// (<see cref="OfflineCode"/>): every operation until the database is reopened.
    /// </summary>
    /// <exception cref="DatabaseOfflineException">The database is offline.</exception>
    internal void ThrowIfOffline()
    {
        if (DataStorage.OfflineError is { } error)
        {
            throw DatabaseOfflineException.Create(OfflineCode, Name, error);
        }
    }

    /// <summary>
    /// Translates a failure the storage's offline state caused into the coded refusal
    /// (<see cref="DatabaseOfflineException"/>), or into
    /// <see cref="DatabaseTransactionCommitUnconfirmedException"/> when a storage commit record
    /// was written before its flush failed
    /// (<see cref="Assimalign.Cohesion.Database.Storage.StorageOfflineException.CommitRecordWritten"/>),
    /// so the work may survive the reopen. An unconfirmed commit that already has its own type is
    /// returned unchanged, and so is any other failure.
    /// </summary>
    /// <param name="error">The failure to translate.</param>
    /// <returns>The translated failure, or <paramref name="error"/> itself.</returns>
    internal Exception TranslateOffline(Exception error)
    {
        if (error is DatabaseOfflineException or DatabaseTransactionCommitUnconfirmedException or TransactionCommitUnconfirmedException
            || Assimalign.Cohesion.Database.Storage.StorageOfflineException.Find(error) is not { } offline)
        {
            return error;
        }

        return offline.CommitRecordWritten
            ? DatabaseTransactionCommitUnconfirmedException.Create(OfflineCode, Name, offline)
            : DatabaseOfflineException.Create(OfflineCode, Name, DataStorage.OfflineError ?? offline);
    }

    public ValueTask<IDocumentCollection> CreateCollectionAsync(string name, CancellationToken cancellationToken = default)
        => CreateCollectionAsync(name, null, cancellationToken);
    internal ValueTask<IDocumentCollection> CreateCollectionAsync(string name, DocumentDatabaseSession? session, CancellationToken token)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        DocumentSystemCollections.EnsureReadOnly(name);
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
        DocumentSystemCollections.EnsureReadOnly(name);
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
        ThrowIfOffline();
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
        catch (Exception error)
        {
            var reported = TranslateOffline(error);
            if (operation is not null)
            {
                await operation.AbortAsync(reported).ConfigureAwait(false);
            }

            if (ReferenceEquals(reported, error)) { throw; }
            throw reported;
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
        catch (Exception error)
        {
            // An explicit transaction records the error its caller sees as the cause of its abort.
            // A failure the offline storage caused is reported with the database's offline code
            // (#1243); the unconfirmed commit that took it offline keeps its own type.
            var offline = TranslateOffline(error);
            var reported = ReferenceEquals(offline, error) ? TranslateKernelFailure(error) : offline;
            await operation.AbortAsync(reported).ConfigureAwait(false);
            if (ReferenceEquals(reported, error)) { throw; }
            throw reported;
        }
    }

    /// <summary>
    /// Translates a failure of the transaction kernel into the area root's exception (the area
    /// error policy: the layer that owns both vocabularies translates at its boundary); any other
    /// failure is returned unchanged. Statements and the explicit transaction's commit and
    /// rollback share it.
    /// </summary>
    /// <param name="error">The failure to translate.</param>
    /// <returns>The translated failure, or <paramref name="error"/> itself.</returns>
    internal static Exception TranslateKernelFailure(Exception error) => error switch
    {
        TransactionDeadlockException => new DatabaseTransactionDeadlockException(error.Message, error),
        TransactionAbortedException => new DatabaseTransactionAbortedException(error.Message, error),
        TransactionCommitUnconfirmedException => new DatabaseTransactionCommitUnconfirmedException(error.Message, error),
        _ => error,
    };

    // One database writer at a time is deliberately conservative. The shared
    // lock manager owns waits and releases; readers remain snapshot based.
    internal async ValueTask LockWriterAsync(ITransactionContext context, CancellationToken token)
    {
        await Coordinator.LockManager.AcquireAsync(context.Sequence, LockResource.Database(), LockMode.Exclusive, token).ConfigureAwait(false);
        try
        {
            // A session may close or its transaction may roll back while this
            // request waits. The end fails the requests it finds queued, but one
            // queued just after it is granted later to the ended owner, which
            // must release that grant before the operation leaves the wait. The
            // kernel sets the state before it releases. While the transaction
            // manager still tracks the owner (a rollback whose undo is deferred),
            // the coordinator's lock manager leaves that release to the manager,
            // which makes it once the undo completes (#1226).
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
            // Safe after a failed coordinator close: a writer whose undo still failed is kept in
            // flight in the storage, so its close does not truncate the journal (#1226).
            DataStorage.Dispose();
        }
    }
    public ValueTask DisposeAsync() { Dispose(); return default; }
}
