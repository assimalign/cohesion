using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Assimalign.Cohesion.Database.Blob.Catalog;
using Assimalign.Cohesion.Database.Blob.Storage;
using Assimalign.Cohesion.Database.Transactions;

namespace Assimalign.Cohesion.Database.Blob.Internal;

internal sealed class BlobDatabaseInstance : IBlobDatabase
{
    private int _disposed;
    internal BlobDatabaseInstance(string name, IDatabaseEngine engine, BlobStorage storage, bool recover)
    {
        Name = name;
        Engine = engine;
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

        if (engine is BlobDatabaseEngine owner)
        {
            // A deferred undo is retried on its own backoff, from about 100 ms up to the
            // maintenance interval, and the purge worker wakes for it (#1226).
            Coordinator.DeferredUndoRetryLimit = owner.EngineOptions.MaintenanceInterval;
            Coordinator.DeferredUndoRetryDelay = owner.EngineOptions.DeferredUndoRetryDelay;
            Coordinator.OnUndoDeferred = owner.UndoDeferredSignal.Set;
        }

        if (recover)
        {
            Coordinator.AnalyzeAndScrub();
            Coordinator.CompleteRecovery();
        }
        Catalog = BlobCatalog.Open(storage, Coordinator);
    }

    public DatabaseName Name { get; }
    public IDatabaseEngine Engine { get; }
    internal BlobStorage DataStorage { get; }
    internal TransactionCoordinator Coordinator { get; }
    internal BlobCatalog Catalog { get; }

    public ValueTask<IDatabaseSession> CreateSessionAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ThrowIfOffline();
        cancellationToken.ThrowIfCancellationRequested();
        return new ValueTask<IDatabaseSession>(new BlobDatabaseSession(this));
    }

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
    /// Refuses an operation on an offline database with <see cref="DatabaseOfflineException"/>
    /// (<see cref="OfflineCode"/>): every operation, in process and over the wire server, until
    /// the database is reopened.
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

    public ValueTask<IBlobContainer> CreateContainerAsync(string name, CancellationToken cancellationToken = default)
        => CreateContainerAsync(name, null, cancellationToken);
    internal ValueTask<IBlobContainer> CreateContainerAsync(string name, BlobDatabaseSession? session, CancellationToken token)
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
            return (IBlobContainer)new BlobContainer(this, metadata, session);
        }, token);
    }

    public ValueTask<IBlobContainer> GetContainerAsync(string name, CancellationToken cancellationToken = default)
        => GetContainerAsync(name, null, cancellationToken);
    internal ValueTask<IBlobContainer> GetContainerAsync(string name, BlobDatabaseSession? session, CancellationToken token)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return RunAsync(session, operation => new ValueTask<IBlobContainer>(new BlobContainer(this,
            Catalog.FindContainer(name, operation.Context.Snapshot) ?? throw new DatabaseException($"Container '{name}' does not exist."), session)), token);
    }

    public ValueTask DropContainerAsync(string name, CancellationToken cancellationToken = default)
        => DropContainerAsync(name, null, cancellationToken);
    internal async ValueTask DropContainerAsync(string name, BlobDatabaseSession? session, CancellationToken token)
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

    public IAsyncEnumerable<IBlobContainer> GetContainersAsync(CancellationToken cancellationToken = default)
        => GetContainersAsync(null, cancellationToken);
    internal async IAsyncEnumerable<IBlobContainer> GetContainersAsync(BlobDatabaseSession? session, [EnumeratorCancellation] CancellationToken token)
    {
        var containers = await RunAsync(session, operation => new ValueTask<IReadOnlyList<BlobContainerMetadata>>(
            Catalog.GetContainers(operation.Context.Snapshot)), token).ConfigureAwait(false);
        foreach (var metadata in containers)
        {
            token.ThrowIfCancellationRequested();
            ThrowIfDisposed();
            session?.ThrowIfNotOpen();
            yield return new BlobContainer(this, metadata, session);
        }
    }

    internal async ValueTask<BlobOperation> BeginOperationAsync(BlobDatabaseSession? session, CancellationToken token)
    {
        ThrowIfDisposed();
        ThrowIfOffline();
        token.ThrowIfCancellationRequested();
        var explicitTransaction = session?.ReserveOperation();
        BlobOperation? operation = null;
        try
        {
            var context = explicitTransaction?.Context ?? await Coordinator.BeginAsync(IsolationLevel.Snapshot, token).ConfigureAwait(false);
            operation = new BlobOperation(this, session, context, explicitTransaction);
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

    internal async ValueTask<T> RunAsync<T>(BlobDatabaseSession? session, Func<BlobOperation, ValueTask<T>> action, CancellationToken token)
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
