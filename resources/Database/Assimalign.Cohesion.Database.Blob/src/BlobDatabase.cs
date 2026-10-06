using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Database.Blob.Catalog;
using Assimalign.Cohesion.Database.Blob.Internal;
using Assimalign.Cohesion.Database.Blob.Storage;
using Assimalign.Cohesion.Database.Transactions;

namespace Assimalign.Cohesion.Database.Blob;

/// <summary>
/// A blob-model database: named containers of streamed large objects with a transactional
/// metadata catalog.
/// </summary>
/// <remarks>
/// <para>
/// A database composes its blob storage, the transaction coordinator every session binds to, and
/// the catalog of containers and blob metadata (<see cref="BlobCatalog"/>). Every container and
/// blob operation takes a session (owner decision 32 of 2026-10-06): the container operations are
/// the session's (<see cref="BlobDatabaseSession.CreateContainerAsync"/> and its siblings), each an
/// operation of it, in its explicit transaction when one is open, and a container is bound to the
/// session that returned it. Until that decision the database had container operations of its
/// own, which ran in autocommit outside any session, as did the operations of a container they
/// returned, so one called through a session's <see cref="BlobDatabaseSession.Database"/> while
/// that session's transaction had written waited for the transaction's writer lock, which only
/// the caller could release.
/// </para>
/// <para>
/// <b>Shape (concrete-types plan, phase 4, #1260).</b> A public sealed leaf of
/// <see cref="DatabaseInstance"/> with an internal constructor, replacing the former
/// <c>IBlobDatabase</c> interface, its internal implementation and the session-bound view a
/// session returned as its database (option B, §6.6: <see cref="BlobDatabaseSession.Database"/>
/// is this unbound database, and disposing it closes the database for every session, never a
/// session itself; once the close ends the engine forgets it, and the engine's
/// <c>OpenDatabaseAsync</c> opens it again from its files, owner decision 33, #1289; until then
/// its workers skip it, so the engine stays <see cref="EngineState.Running"/>). The engine creates
/// and opens it. The base owns the name, the owning engine (re-exposed typed with <c>new</c>) and
/// the disposed flag.
/// </para>
/// </remarks>
public sealed class BlobDatabase : DatabaseInstance
{
    private readonly BlobDatabaseEngine _engine;

    internal BlobDatabase(DatabaseName name, BlobDatabaseEngine engine, BlobStorage storage, bool recover)
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

        if (recover)
        {
            Coordinator.AnalyzeAndScrub();
            Coordinator.CompleteRecovery();
        }
        Catalog = BlobCatalog.Open(storage, Coordinator);
    }

    /// <summary>
    /// Gets the blob engine that owns this database.
    /// </summary>
    public new BlobDatabaseEngine Engine => _engine;

    /// <summary>
    /// Gets the data storage file set, for the engine's background workers.
    /// </summary>
    internal BlobStorage DataStorage { get; }

    /// <summary>
    /// Gets the database's transaction coordinator (the MVCC composition sessions bind to), for
    /// the engine's background workers and tests.
    /// </summary>
    internal TransactionCoordinator Coordinator { get; }

    /// <summary>
    /// Gets the database's catalog of containers and blob metadata.
    /// </summary>
    internal BlobCatalog Catalog { get; }

    /// <summary>
    /// The code that leads the message of every operation refused because the database is
    /// offline (#1243).
    /// </summary>
    internal const string OfflineCode = "COHDBB002";

    /// <summary>
    /// Gets whether a failed durable flush took the database offline.
    /// </summary>
    internal bool IsOffline => DataStorage.IsOffline;

    /// <summary>
    /// Gets whether the database's close has started: by the engine, or by a holder of the
    /// database, a session's <see cref="BlobDatabaseSession.Database"/> included. The engine keeps
    /// a database its holder is closing registered until the close ends, then forgets it; its
    /// workers skip it meanwhile.
    /// </summary>
    internal bool IsClosed => IsDisposed;

    /// <summary>
    /// Creates a new lightweight blob session scoped to this database.
    /// </summary>
    /// <param name="cancellationToken">Observed before the session is created.</param>
    /// <returns>A new session.</returns>
    /// <exception cref="ObjectDisposedException">The database has been disposed.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was canceled.</exception>
    /// <exception cref="DatabaseOfflineException">The database is offline (<c>COHDBB002</c>, #1243).</exception>
    public new async ValueTask<BlobDatabaseSession> CreateSessionAsync(CancellationToken cancellationToken = default)
        => (BlobDatabaseSession)await base.CreateSessionAsync(cancellationToken).ConfigureAwait(false);

    /// <summary>
    /// Refuses an operation on an offline database with <see cref="DatabaseOfflineException"/>
    /// (<see cref="OfflineCode"/>): every operation, in process and over the wire server, until
    /// the database is reopened.
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
    /// check, for the model's sessions, operations, containers and engine.
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

    /// <summary>
    /// Translates an operation's failure: one the offline storage caused becomes the coded
    /// refusal (#1243), any other kernel failure the area root's exception.
    /// </summary>
    /// <param name="error">The failure to translate.</param>
    /// <returns>The translated failure, or <paramref name="error"/> itself.</returns>
    internal Exception TranslateFailure(Exception error)
    {
        var offline = TranslateOffline(error);
        return ReferenceEquals(offline, error) ? TranslateKernelFailure(error) : offline;
    }

    /// <summary>
    /// Translates a failure of the transaction kernel into the area root's exception (the area
    /// error policy: the layer that owns both vocabularies translates at its boundary); any other
    /// failure is returned unchanged. Operations and the explicit transaction's commit and
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

    internal ValueTask<BlobContainer> CreateContainerAsync(string name, BlobDatabaseSession session, CancellationToken token)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return RunAsync(session, async operation =>
        {
            await LockWriterAsync(operation.Context, token).ConfigureAwait(false);
            var previous = Catalog.FindContainer(name, operation.Context.Snapshot);
            if (previous != Catalog.FindContainer(name, LatestSnapshot(operation.Context)))
            {
                ThrowConflict();
            }

            if (previous is not null)
            {
                throw new DatabaseException($"Container '{name}' already exists.");
            }

            var metadata = new BlobContainerMetadata(Guid.NewGuid(), name);
            await Catalog.SaveContainerAsync(metadata, operation.Context, token).ConfigureAwait(false);
            return new BlobContainer(this, metadata, session);
        }, token);
    }

    internal ValueTask<BlobContainer> GetContainerAsync(string name, BlobDatabaseSession session, CancellationToken token)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return RunAsync(session, operation => new ValueTask<BlobContainer>(new BlobContainer(this,
            Catalog.FindContainer(name, operation.Context.Snapshot) ?? throw new DatabaseException($"Container '{name}' does not exist."), session)), token);
    }

    internal async ValueTask DropContainerAsync(string name, BlobDatabaseSession session, CancellationToken token)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        await RunAsync(session, async operation =>
        {
            await LockWriterAsync(operation.Context, token).ConfigureAwait(false);
            var container = Catalog.FindContainer(name, operation.Context.Snapshot)
                ?? throw new DatabaseException($"Container '{name}' does not exist.");
            if (container != Catalog.FindContainer(name, LatestSnapshot(operation.Context)))
            {
                ThrowConflict();
            }
            // Same authority rule as SqlPlanExecutor.EnsureCanChange. Blob has
            // no provisioning authority, so every schema-owned drop is locked.
            if (container.Owner == DatabaseObjectOwner.Schema)
            {
                throw new DatabaseObjectLockedException(container.Name, container.OwningSchema!, "DROP CONTAINER");
            }

            var visible = Catalog.GetBlobs(container.Id, null, operation.Context.Snapshot);
            if (!visible.SequenceEqual(Catalog.GetBlobs(container.Id, null, LatestSnapshot(operation.Context))))
            {
                ThrowConflict();
            }

            foreach (var blob in visible)
            {
                await DataStorage.TombstoneContentAsync(Coordinator, operation.Context, Content(blob), token).ConfigureAwait(false);
                await Catalog.DeleteBlobAsync(container.Id, blob.Name, operation.Context, token).ConfigureAwait(false);
            }
            await Catalog.DeleteContainerAsync(container.Id, operation.Context, token).ConfigureAwait(false);
            return true;
        }, token).ConfigureAwait(false);
    }

    internal async IAsyncEnumerable<BlobContainer> GetContainersAsync(BlobDatabaseSession session, [EnumeratorCancellation] CancellationToken token)
    {
        var containers = await RunAsync(session, operation => new ValueTask<IReadOnlyList<BlobContainerMetadata>>(
            Catalog.GetContainers(operation.Context.Snapshot)), token).ConfigureAwait(false);
        foreach (var metadata in containers)
        {
            token.ThrowIfCancellationRequested();
            ThrowIfDisposed();
            session.ThrowIfNotOpen();
            yield return new BlobContainer(this, metadata, session);
        }
    }

    /// <summary>
    /// Starts one operation of a session: holds the session and admits the operation into the
    /// session's explicit transaction, or begins an autocommit context when none is open, and pins
    /// a read-committed statement snapshot. A failure releases whatever the operation took.
    /// </summary>
    /// <param name="session">The session the operation runs on.</param>
    /// <param name="token">Cancellation token for the start.</param>
    /// <returns>The running operation.</returns>
    internal async ValueTask<BlobOperation> BeginOperationAsync(BlobDatabaseSession session, CancellationToken token)
    {
        ThrowIfDisposed();
        ThrowIfOffline();
        token.ThrowIfCancellationRequested();
        var explicitTransaction = session.EnterOperation();
        BlobOperation? operation = null;
        try
        {
            var context = explicitTransaction?.Context ?? await Coordinator.BeginAsync(IsolationLevel.Snapshot, token).ConfigureAwait(false);
            operation = new BlobOperation(this, session, context, explicitTransaction);
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

    internal async ValueTask<T> RunAsync<T>(BlobDatabaseSession session, Func<BlobOperation, ValueTask<T>> action, CancellationToken token)
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
            var reported = TranslateFailure(error);
            await operation.AbortAsync(reported).ConfigureAwait(false);
            if (ReferenceEquals(reported, error)) { throw; }
            throw reported;
        }
    }

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
            // request waits. ReleaseAll at the end fails the requests it finds
            // queued, but one queued just after it is granted later to the ended
            // owner, which must release that grant before the operation leaves
            // the wait; otherwise the database writer lock stays granted to an
            // ended transaction. The kernel sets the state before it releases.
            // While the transaction manager still tracks the owner (a rollback
            // whose undo is deferred), the coordinator's lock manager leaves that
            // release to the manager, which makes it once the undo completes (#1226).
            token.ThrowIfCancellationRequested();
            ThrowIfDisposed();
            ThrowIfOffline();
            if (context.State != TransactionState.Active)
            {
                throw new DatabaseException("The blob operation's transaction ended while waiting for the writer lock.");
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

    internal static void ThrowConflict() => throw new DatabaseTransactionAbortedException("The blob catalog changed since this transaction's snapshot. Retry the transaction.");
    internal static BlobContentReference Content(BlobCatalogEntry entry) => new(entry.HeadLocation, entry.Length, entry.Checksum);
    internal static BlobProperties Properties(BlobCatalogEntry entry)
        => new(entry.Name, entry.Length, entry.ContentType, entry.ETag, entry.CreatedAt, entry.ModifiedAt, entry.Checksum);

    /// <inheritdoc />
    protected override ValueTask<DatabaseSession> CreateSessionCoreAsync(CancellationToken cancellationToken)
    {
        ThrowIfOffline();
        return new ValueTask<DatabaseSession>(new BlobDatabaseSession(this));
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
