using System;
using System.Globalization;
using System.Threading.Tasks;

using Assimalign.Cohesion.Database.Transactions;

namespace Assimalign.Cohesion.Database.Blob;

/// <summary>
/// An explicit blob transaction, over an MVCC transaction context from the database's transaction
/// coordinator. An operation that fails inside it (a container operation of the session, or a
/// blob operation or stream of a container bound to the session) aborts the whole transaction
/// (#1225, the contract #1188 set for Graph): the engine rolls its work back at once, and the
/// transaction stays the session's transaction, reporting <see cref="TransactionState.Faulted"/>,
/// until the caller ends it.
/// </summary>
/// <remarks>
/// <para>
/// <b>The end state machine is the root base's</b> (<see cref="DatabaseTransaction"/>, the
/// #1188, #1225 and #1226 contracts): until the caller ends an aborted transaction, the session
/// refuses every operation and BEGIN with <c>CB04001</c>; a rollback ends it, and a commit ends
/// it with <c>CB04001</c> and commits nothing, as every later commit does. A transaction that
/// did not commit accepts any number of rollbacks, a token is observed only before a commit or
/// rollback starts, and a commit is refused while an operation of the transaction (an open blob
/// stream included) still runs. A commit or rollback that started always ends the context: the
/// transaction kernel completes a started rollback whatever fails or is canceled, and aborts a
/// commit it cannot complete (#1226). This type supplies the model's vocabulary only: the kernel
/// state, the kernel commit and rollback with the engine's exception translation, the offline
/// refusal (<c>CB06002</c>, #1243) and the <c>CB04001</c> aborted error. Blob DESIGN.md,
/// "Failed operations in explicit transactions", records the contract and the reference engines
/// it follows.
/// </para>
/// <para>
/// <b>Shape (concrete-types plan, phase 4, #1260).</b> A public sealed leaf of the root base with
/// an internal constructor; the session that began it is the only way to create one. It replaces
/// the model's internal transaction, which carried its own copy of the end state machine.
/// </para>
/// </remarks>
[global::System.Diagnostics.DebuggerDisplay("BlobDatabaseTransaction Id={Id}")]
[global::System.Diagnostics.DebuggerTypeProxy(typeof(global::Assimalign.Cohesion.Database.Blob.BlobDatabaseTransaction.DebugView))]
public sealed class BlobDatabaseTransaction : DatabaseTransaction
{

    // The database whose offline state (#1243) refuses the transaction's commit and rollback and
    // makes its disposal touch nothing.
    private readonly BlobDatabase _database;

    internal BlobDatabaseTransaction(TransactionCoordinator coordinator, TransactionContext context, BlobDatabase database)
        : base(coordinator, context)
    {
        _database = database;
    }

    /// <summary>
    /// Gets the MVCC transaction context the transaction's operations run under.
    /// </summary>
    internal TransactionContext Context => KernelContext;

    /// <summary>
    /// Gets whether operations may run in the transaction: the caller has not ended it, no
    /// operation aborted it, and its kernel transaction is active.
    /// </summary>
    internal bool AcceptsOperations => IsUsable;

    /// <summary>
    /// Admits one operation into the transaction, so a commit cannot start while it runs. Every
    /// admitted operation is paired with <see cref="EndBlobOperation"/>.
    /// </summary>
    /// <returns>True when the transaction accepts the operation; false when it refuses operations.</returns>
    internal bool TryBeginBlobOperation() => TryBeginOperation();

    /// <summary>Ends one operation <see cref="TryBeginBlobOperation"/> admitted.</summary>
    internal void EndBlobOperation() => EndOperation();

    /// <summary>
    /// Creates the session's refusal of an operation the transaction did not admit: the base's
    /// refusal, for the session.
    /// </summary>
    /// <returns>The refusal.</returns>
    internal DatabaseException CreateOperationRefusal() => CreateRefusal();

    /// <summary>
    /// Aborts the transaction because an operation failed in it (#1225): the base records the
    /// first failure as the cause and rolls the kernel transaction back, so it holds no writer
    /// lock while it waits for the caller's rollback.
    /// </summary>
    /// <param name="cause">The failure the caller observed from the operation.</param>
    /// <returns>A task that completes once the rollback ended.</returns>
    internal ValueTask AbortForFailedOperationAsync(Exception cause) => AbortAsync(cause);

    /// <inheritdoc />
    protected override TransactionState GetKernelState() => KernelContext.State;

    /// <inheritdoc />
    /// <remarks>
    /// While the database closes, the kernel refuses every end before it starts: a commit then
    /// fails with <see cref="ObjectDisposedException"/>, and the base reports the transaction ended
    /// with nothing committed. A commit whose record was written but could not be made durable
    /// throws <see cref="DatabaseTransactionCommitUnconfirmedException"/> and leaves the transaction
    /// <c>Committed</c>.
    /// </remarks>
    protected override async ValueTask CommitCoreAsync()
    {
        try
        {
            // Like a rollback, a commit that started runs to completion: a cancellation here would
            // only turn into a kernel abort of work the caller asked to keep.
            await KernelCoordinator.CommitAsync(KernelContext).ConfigureAwait(false);
        }
        catch (Exception error)
        {
            // A kernel abort of the commit crosses the engine boundary as the area root's exception.
            var translated = _database.TranslateFailure(error);
            if (ReferenceEquals(translated, error))
            {
                throw;
            }

            throw translated;
        }
    }

    /// <inheritdoc />
    /// <remarks>
    /// The caller's token is not passed on (PostgreSQL holds interrupts through
    /// <c>AbortTransaction</c> for the same reason), and the kernel ends the context whatever
    /// fails once it starts (#1226). Only a closing database refuses the rollback before it starts,
    /// and its disposal then aborts the context itself.
    /// </remarks>
    protected override async ValueTask RollbackCoreAsync()
    {
        try
        {
            await KernelCoordinator.RollbackAsync(KernelContext).ConfigureAwait(false);
        }
        catch (Exception error)
        {
            var translated = _database.TranslateFailure(error);
            if (ReferenceEquals(translated, error))
            {
                throw;
            }

            throw translated;
        }
    }

    /// <inheritdoc />
    protected override DatabaseException? GetOfflineRefusal()
    {
        if (TransactionCoordinator.IsPartialUndoPending(KernelContext))
        { ObjectDisposedException.ThrowIf(_database.IsClosed, _database); }
        return _database.GetOfflineRefusal();
    }

    /// <inheritdoc />
    protected override DatabaseException CreateAbortedException(Exception? cause, bool commit)
    {
        // The cause travels in the message as well, so it survives any boundary that keeps only
        // text: the wire server reports a failure by its message.
        string reason = cause is null ? string.Empty : string.Format(CultureInfo.CurrentCulture, ErrorMessages.TransactionFailureCause, cause.Message);
        string message = commit
            ? ErrorMessages.TransactionCommitAborted + reason
            : ErrorMessages.TransactionOperationsAborted + reason;
        return new DatabaseException(message, [new Types.Diagnostic
        { Code = "CB04001", Message = message, Severity = Types.DiagnosticSeverity.Error }], cause);
    }

    private sealed class DebugView
    {
        private readonly global::Assimalign.Cohesion.Database.Blob.BlobDatabaseTransaction _target;

        public DebugView(global::Assimalign.Cohesion.Database.Blob.BlobDatabaseTransaction value)
        {
            _target = value;
        }

        public global::Assimalign.Cohesion.Database.Transactions.TransactionId Id => _target.Id;
        public global::Assimalign.Cohesion.Database.Transactions.IsolationLevel IsolationLevel => _target.IsolationLevel;
    }
}
