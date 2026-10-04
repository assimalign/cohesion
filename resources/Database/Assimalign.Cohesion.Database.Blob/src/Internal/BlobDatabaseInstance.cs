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
    internal IBlobCatalog Catalog { get; }

    public ValueTask<IDatabaseSession> CreateSessionAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        cancellationToken.ThrowIfCancellationRequested();
        return new ValueTask<IDatabaseSession>(new BlobDatabaseSession(this));
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
            if (operation is not null)
            {
                await operation.AbortAsync(error).ConfigureAwait(false);
            }
            throw;
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
            var reported = TranslateKernelFailure(error);
            await operation.AbortAsync(reported).ConfigureAwait(false);
            if (ReferenceEquals(reported, error)) { throw; }
            throw reported;
        }
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
    internal TransactionSnapshot LatestSnapshot(ITransactionContext context) => new(context.Sequence,
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
