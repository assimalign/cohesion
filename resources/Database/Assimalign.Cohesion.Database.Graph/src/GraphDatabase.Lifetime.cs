using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Database.Graph.Internal;
using Assimalign.Cohesion.Database.Graph.Storage;
using Assimalign.Cohesion.Database.Storage;
using Assimalign.Cohesion.Database.Transactions;

namespace Assimalign.Cohesion.Database.Graph;

public sealed partial class GraphDatabase
{
    /// <summary>
    /// Starts one statement on a session: holds the session, admits the statement into the
    /// session's explicit transaction or begins an autocommit context, and pins a read-committed
    /// statement snapshot. A failure releases whatever the statement took.
    /// </summary>
    /// <param name="session">The session the statement runs on.</param>
    /// <param name="token">Cancellation token for the start.</param>
    /// <returns>The running statement.</returns>
    internal async ValueTask<GraphOperation> BeginOperationAsync(GraphDatabaseSession session, CancellationToken token)
    {
        ThrowIfDisposed();
        ThrowIfOffline();
        token.ThrowIfCancellationRequested();
        var explicitTransaction = session.EnterOperation();
        GraphOperation? operation = null;
        try
        {
            var context = explicitTransaction?.Context ?? await Coordinator.BeginAsync(IsolationLevel.Snapshot, token).ConfigureAwait(false);
            operation = new GraphOperation(this, session, context, explicitTransaction);
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

    internal async ValueTask<T> RunAsync<T>(GraphDatabaseSession session, Func<GraphOperation, ValueTask<T>> action, CancellationToken token)
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
            var reported = Translate(error);
            await operation.AbortAsync(reported).ConfigureAwait(false);
            if (ReferenceEquals(reported, error)) { throw; }
            throw reported;
        }
    }

    // Child-root failures cross the engine boundary as the area root's exceptions. A failure the
    // offline storage caused is the database's coded refusal (#1243), checked first: the offline
    // error is a StorageException, which the kernel translation would report as COHDBG006.
    private Exception Translate(Exception error) => TranslateOffline(error) is var offline && !ReferenceEquals(offline, error)
        ? offline
        : TranslateStatementFailure(error);

    private static Exception TranslateStatementFailure(Exception error) => TranslateKernelFailure(error) switch
    {
        var translated when !ReferenceEquals(translated, error) => translated,
        _ when error is InvalidOperationException => new DatabaseException("COHDBG003: " + error.Message, error),
        // A label expression or predicate nested deeper than this thread's stack: the walks
        // check the stack before they descend, so the statement fails, not the process.
        _ when error is InsufficientExecutionStackException stack => GraphStatementTooComplex.Create(stack),
        _ => error,
    };

    /// <summary>
    /// Translates a failure of the transaction kernel or the storage child root into the area
    /// root's exception; any other failure is returned unchanged. Statements and the explicit
    /// transaction's commit and rollback share it.
    /// </summary>
    /// <param name="error">The failure to translate.</param>
    /// <returns>The translated failure, or <paramref name="error"/> itself.</returns>
    internal static Exception TranslateKernelFailure(Exception error) => error switch
    {
        TransactionDeadlockException => new DatabaseTransactionDeadlockException(error.Message, error),
        TransactionAbortedException => new DatabaseTransactionAbortedException(error.Message, error),
        TransactionCommitUnconfirmedException => new DatabaseTransactionCommitUnconfirmedException(error.Message, error),
        // An element whose record or index key outgrows storage fails its statement; the store
        // wrote nothing for it. It derives from StorageException, so it is matched first.
        GraphElementTooLargeException => new DatabaseException("COHDBG009: " + error.Message, error),
        StorageException => new DatabaseException("COHDBG006: " + error.Message, error),
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
    internal TransactionSnapshot LatestSnapshot(TransactionContext context) => new(context.Sequence,
        TransactionSequence.None, new TransactionSequence(ulong.MaxValue), Coordinator.GetOpenContexts().Select(item => item.Sequence));

    internal static void ThrowConflict() => throw new DatabaseTransactionAbortedException("The graph catalog changed since this transaction's snapshot. Retry the transaction.");

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
