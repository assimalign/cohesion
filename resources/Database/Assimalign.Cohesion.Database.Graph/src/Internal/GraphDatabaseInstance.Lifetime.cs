using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Assimalign.Cohesion.Database.Transactions;
using Assimalign.Cohesion.Database.Storage;
namespace Assimalign.Cohesion.Database.Graph.Internal;
internal sealed partial class GraphDatabaseInstance
{
    internal async ValueTask<GraphOperation> BeginOperationAsync(GraphDatabaseSession? session, CancellationToken token)
    {
        ThrowIfDisposed();
        token.ThrowIfCancellationRequested();
        var explicitTransaction = session?.ReserveOperation();
        GraphOperation? operation = null;
        try
        {
            var context = explicitTransaction?.Context ?? await Coordinator.BeginAsync(IsolationLevel.Snapshot, token).ConfigureAwait(false);
            operation = new GraphOperation(this, session, context, explicitTransaction);
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

    internal async ValueTask<T> RunAsync<T>(GraphDatabaseSession? session, Func<GraphOperation, ValueTask<T>> action, CancellationToken token)
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
            await operation.AbortAsync().ConfigureAwait(false);
            if (error is TransactionDeadlockException) { throw new DatabaseTransactionDeadlockException(error.Message, error); }
            if (error is TransactionAbortedException) { throw new DatabaseTransactionAbortedException(error.Message, error); }
            if (error is StorageException) { throw new DatabaseException("COHDBG006: " + error.Message, error); }
            if (error is InvalidOperationException) { throw new DatabaseException("COHDBG003: " + error.Message, error); }
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
                throw new DatabaseException("The graph operation's transaction ended while waiting for the writer lock.");
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

    internal static void ThrowConflict() => throw new DatabaseTransactionAbortedException("The graph catalog changed since this transaction's snapshot. Retry the transaction.");
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
