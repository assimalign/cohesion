using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Database.Documents.Catalog;
using Assimalign.Cohesion.Database.Documents.Internal;
using Assimalign.Cohesion.Database.Documents.Storage;
using Assimalign.Cohesion.Database.Transactions;

namespace Assimalign.Cohesion.Database.Documents;

/// <summary>
/// A document-model database: named collections of versioned documents.
/// </summary>
/// <remarks>
/// <para>
/// A database composes its document storage, the transaction coordinator every session binds to,
/// and the catalog of collections, document versions and indexes (<see cref="DocumentCatalog"/>).
/// Every collection and document operation takes a session (owner decision 32 of 2026-10-06): the
/// collection operations are the session's (<see cref="DocumentDatabaseSession.CreateCollectionAsync"/>
/// and its siblings), each a statement of it, in its explicit transaction when one is open, and a
/// collection's document operations take the session they run in. Until that decision the database
/// had collection operations of its own, which ran in autocommit outside any session, so one called
/// through a session's <see cref="DocumentDatabaseSession.Database"/> while that session's
/// transaction had written waited for the transaction's writer lock, which only the caller could
/// release.
/// </para>
/// <para>
/// <b>Shape (concrete-types plan, phase 4, #1260).</b> A public sealed leaf of
/// <see cref="DatabaseInstance"/> with an internal constructor, replacing the former
/// <c>IDocumentDatabase</c> interface, its internal implementation and the session-bound view a
/// session returned as its database (option B, §6.6: <see cref="DocumentDatabaseSession.Database"/>
/// is this unbound database, and disposing it closes the database for every session, never a
/// session itself; once the close ends the engine forgets it, and the engine's
/// <c>OpenDatabaseAsync</c> opens it again from its files, owner decision 33, #1289; until then
/// its workers skip it, so the engine stays <see cref="EngineState.Running"/>). The engine creates
/// and opens it. The base owns the name, the owning engine (re-exposed typed with <c>new</c>) and
/// the disposed flag.
/// </para>
/// </remarks>
public sealed class DocumentDatabase : DatabaseInstance
{
    private readonly DocumentDatabaseEngine _engine;

    internal DocumentDatabase(DatabaseName name, DocumentDatabaseEngine engine, DocumentStorage storage, bool recover)
        : base(name, engine)
    {
        _engine = engine;
        DataStorage = storage;
        Coordinator = new TransactionCoordinator(storage, storage.WriteAheadJournal, storage.Records);

        // A wait for the database writer lock ends when the database goes offline (#1268 review):
        // an offline database undoes nothing, so the writer that holds the lock keeps it until the
        // reopen, and a writer queued behind it would otherwise wait that long.
        storage.OnOffline = Coordinator.AbandonLockWaits;
        if (storage.OfflineError is { } alreadyOffline)
        {
            Coordinator.AbandonLockWaits(alreadyOffline);
        }

        // A deferred undo is retried on its own backoff, from about 100 ms up to the
        // maintenance interval, and the purge worker wakes for it (#1226).
        Coordinator.DeferredUndoRetryLimit = engine.EngineOptions.MaintenanceInterval;
        Coordinator.DeferredUndoRetryDelay = engine.EngineOptions.DeferredUndoRetryDelay;
        Coordinator.OnUndoDeferred = engine.UndoDeferredSignal.Set;

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
            // The stop closes the activity the start opened on this flow even when the recovery
            // throws; the root's failed open reports the failure itself.
            long recoveryStarted = DocumentDatabaseEventSource.Log.IndexRecoveryStart(name, recovery.Aborted.Count);
            bool recovered = false;
            try
            {
                Catalog.RecoverIndexesAsync(recovery.Aborted).AsTask().GetAwaiter().GetResult();
                Coordinator.CompleteRecovery();
                recovered = true;
            }
            finally
            {
                DocumentDatabaseEventSource.Log.IndexRecoveryStop(name, recovered, recoveryStarted);
            }
        }
        else { Catalog = DocumentCatalog.Open(storage, Coordinator); }
    }

    /// <summary>
    /// Gets the document engine that owns this database.
    /// </summary>
    public new DocumentDatabaseEngine Engine => _engine;

    /// <summary>
    /// Gets the data storage file set, for the engine's background workers.
    /// </summary>
    internal DocumentStorage DataStorage { get; }

    /// <summary>
    /// Gets the database's transaction coordinator (the MVCC composition sessions bind to), for
    /// the engine's background workers and tests.
    /// </summary>
    internal TransactionCoordinator Coordinator { get; }

    /// <summary>
    /// Gets the database's catalog of collections, document versions and indexes.
    /// </summary>
    internal DocumentCatalog Catalog { get; }

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
    /// Gets whether the database's close has started: by the engine, or by a holder of the
    /// database, a session's <see cref="DocumentDatabaseSession.Database"/> included. The engine
    /// keeps a database its holder is closing registered until the close ends, then forgets it; its
    /// workers skip it meanwhile.
    /// </summary>
    internal bool IsClosed => IsDisposed;

    /// <summary>
    /// Creates a new lightweight document session scoped to this database.
    /// </summary>
    /// <param name="cancellationToken">Observed before the session is created.</param>
    /// <returns>A new session.</returns>
    /// <exception cref="ObjectDisposedException">The database has been disposed.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was canceled.</exception>
    /// <exception cref="DatabaseOfflineException">The database is offline (<c>COHDBD002</c>, #1243).</exception>
    public new async ValueTask<DocumentDatabaseSession> CreateSessionAsync(CancellationToken cancellationToken = default)
        => (DocumentDatabaseSession)await base.CreateSessionAsync(cancellationToken).ConfigureAwait(false);

    /// <summary>
    /// Refuses an operation on an offline database with <see cref="DatabaseOfflineException"/>
    /// (<see cref="OfflineCode"/>): every operation until the database is reopened.
    /// </summary>
    /// <exception cref="DatabaseOfflineException">The database is offline.</exception>
    internal void ThrowIfOffline()
    {
        if (GetOfflineRefusal() is { } refusal)
        {
            throw refusal;
        }
    }

    /// <summary>
    /// Gets the coded refusal (<see cref="OfflineCode"/>) of an operation on the database while it
    /// is offline, or null while it is online: the vocabulary the root transaction base reads
    /// before a commit or rollback, and before each kernel rollback, which an offline database skips.
    /// </summary>
    /// <returns>The refusal, or null.</returns>
    internal DatabaseOfflineException? GetOfflineRefusal()
        => DataStorage.OfflineError is { } error ? DatabaseOfflineException.Create(OfflineCode, Name, error) : null;

    /// <summary>
    /// Throws <see cref="ObjectDisposedException"/> once the database has been disposed: the base's
    /// check, for the model's sessions, statements, collections and engine.
    /// </summary>
    /// <exception cref="ObjectDisposedException">The database has been disposed.</exception>
    internal void EnsureNotDisposed() => ThrowIfDisposed();

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

    internal ValueTask<DocumentCollection> CreateCollectionAsync(string name, DocumentDatabaseSession session, CancellationToken token)
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
            return new DocumentCollection(this, metadata, session);
        }, token);
    }

    internal ValueTask<DocumentCollection> GetCollectionAsync(string name, DocumentDatabaseSession session, CancellationToken token)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return RunAsync(session, operation => new ValueTask<DocumentCollection>(new DocumentCollection(this,
            Catalog.FindCollection(name, operation.Context.Snapshot) ?? throw new DatabaseException($"Collection '{name}' does not exist."), session)), token);
    }

    internal async ValueTask DropCollectionAsync(string name, DocumentDatabaseSession session, CancellationToken token)
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

    internal async IAsyncEnumerable<DocumentCollection> GetCollectionsAsync(DocumentDatabaseSession session, [EnumeratorCancellation] CancellationToken token)
    {
        var collections = await RunAsync(session, operation => new ValueTask<IReadOnlyList<DocumentCollectionMetadata>>(
            Catalog.GetCollections(operation.Context.Snapshot)), token).ConfigureAwait(false);
        foreach (var metadata in collections)
        {
            token.ThrowIfCancellationRequested();
            ThrowIfDisposed();
            session.ThrowIfNotOpen();
            yield return new DocumentCollection(this, metadata, session);
        }
    }

    /// <summary>
    /// Starts one statement of a session: holds the session and admits the statement into the
    /// session's explicit transaction, or begins an autocommit context when none is open, and pins
    /// a read-committed statement snapshot. A failure releases whatever the statement took.
    /// </summary>
    /// <param name="session">The session the statement runs on.</param>
    /// <param name="token">Cancellation token for the start.</param>
    /// <returns>The running statement.</returns>
    internal async ValueTask<DocumentOperation> BeginOperationAsync(DocumentDatabaseSession session, CancellationToken token)
    {
        ThrowIfDisposed();
        ThrowIfOffline();
        token.ThrowIfCancellationRequested();
        var explicitTransaction = session.EnterOperation();
        DocumentOperation? operation = null;
        try
        {
            var context = explicitTransaction?.Context ?? await Coordinator.BeginAsync(IsolationLevel.Snapshot, token).ConfigureAwait(false);
            operation = new DocumentOperation(this, session, context, explicitTransaction);
            await operation.InitializeAsync(token).ConfigureAwait(false);
            session.Track(operation);
            return operation;
        }
        catch (Exception error)
        {
            var reported = TranslateOffline(error);
            if (operation is not null)
            {
                // The operation owns the session hold and the admission, and releases them as it ends.
                await operation.AbortAsync(reported).ConfigureAwait(false);
            }
            else
            {
                session.ReleaseOperation(explicitTransaction);
            }

            if (ReferenceEquals(reported, error)) { throw; }
            throw reported;
        }
    }

    internal async ValueTask<T> RunAsync<T>(DocumentDatabaseSession session, Func<DocumentOperation, ValueTask<T>> action, CancellationToken token)
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
    /// rollback share it. An unconfirmed commit leads with <see cref="OfflineCode"/>, as on every
    /// other unconfirmed path (owner decision 24 of 2026-10-06, #1272).
    /// </summary>
    /// <param name="error">The failure to translate.</param>
    /// <returns>The translated failure, or <paramref name="error"/> itself.</returns>
    internal Exception TranslateKernelFailure(Exception error) => error switch
    {
        TransactionDeadlockException => new DatabaseTransactionDeadlockException(error.Message, error),
        TransactionAbortedException => new DatabaseTransactionAbortedException(error.Message, error),
        TransactionCommitUnconfirmedException unconfirmed => DatabaseTransactionCommitUnconfirmedException.Create(OfflineCode, Name, unconfirmed),
        _ => error,
    };

    // One database writer at a time is deliberately conservative. The shared
    // lock manager owns waits and releases; readers remain snapshot based.
    internal async ValueTask LockWriterAsync(TransactionContext context, CancellationToken token)
    {
        // An offline database grants no new writer (#1243). A wait for the lock ends with the
        // coded refusal when the database goes offline: the coordinator fails it with the
        // storage's offline error (TransactionCoordinator.AbandonLockWaits), which the caller
        // translates.
        ThrowIfOffline();
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
            ThrowIfOffline();
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
    internal TransactionSnapshot LatestSnapshot(TransactionContext context) => new(context.Sequence,
        TransactionSequence.None, new TransactionSequence(ulong.MaxValue), Coordinator.GetOpenContexts().Select(item => item.Sequence));

    internal static void ThrowConflict() => throw new DatabaseTransactionAbortedException("The document catalog changed since this transaction's snapshot. Retry the transaction.");
    internal static DocumentContentReference Content(DocumentCatalogEntry entry) => new(entry.HeadLocation, entry.Length, entry.Checksum);
    internal Document ReadDocument(DocumentCatalogEntry entry)
        => new(new DocumentId(entry.Id), new DocumentVersion(entry.Version), DataStorage.ReadContent(Content(entry)));

    /// <inheritdoc />
    protected override ValueTask<DatabaseSession> CreateSessionCoreAsync(CancellationToken cancellationToken)
    {
        ThrowIfOffline();
        return new ValueTask<DatabaseSession>(new DocumentDatabaseSession(this));
    }

    /// <inheritdoc />
    /// <remarks>
    /// The coordinator first: it aborts every still-active transaction while the storage is open.
    /// The storage closes even when the coordinator reports a writer whose undo still failed: it
    /// kept that writer in flight in the storage, so the close does not truncate the journal
    /// recovery classifies the writer from (#1226).
    /// </remarks>
    protected override void DisposeCore()
    {
        try
        {
            Coordinator.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
        finally
        {
            DataStorage.Dispose();
        }
    }

    /// <inheritdoc />
    /// <remarks>See <see cref="DisposeCore"/>: the coordinator first, then the storage.</remarks>
    protected override async ValueTask DisposeAsyncCore()
    {
        try
        {
            await Coordinator.DisposeAsync().ConfigureAwait(false);
        }
        finally
        {
            await DataStorage.DisposeAsync().ConfigureAwait(false);
        }
    }
}
