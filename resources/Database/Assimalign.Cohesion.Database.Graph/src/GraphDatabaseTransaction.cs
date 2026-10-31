using System;
using System.Globalization;
using System.Threading.Tasks;

using Assimalign.Cohesion.Database.Transactions;
using Assimalign.Cohesion.Database.Graph.Internal;

namespace Assimalign.Cohesion.Database.Graph;

/// <summary>
/// An explicit graph transaction, over an MVCC transaction context from the database's
/// transaction coordinator. A statement that fails inside it aborts the whole transaction
/// (#1188): the engine rolls its work back at once, and the transaction stays the session's
/// transaction, reporting <see cref="TransactionState.Faulted"/>, until the caller ends it.
/// </summary>
/// <remarks>
/// <para>
/// <b>The end state machine is the root base's</b> (<see cref="DatabaseTransaction"/>, the
/// #1188, #1225 and #1226 contracts): until the caller ends an aborted transaction, the session
/// refuses every statement and BEGIN with <c>CG04007</c>; a rollback ends it, and a commit ends
/// it with <c>CG04007</c> and commits nothing, as every later commit does. A transaction that
/// did not commit accepts any number of rollbacks, a token is observed only before a commit or
/// rollback starts, and a commit is refused while a statement of the transaction still runs. A
/// commit or rollback that started always ends the context: the transaction kernel completes a
/// started rollback whatever fails or is canceled, and aborts a commit it cannot complete (#1226).
/// This type supplies the model's vocabulary only: the kernel state, the kernel commit and
/// rollback with the engine's exception translation, the offline refusal (<c>CG06012</c>,
/// #1243) and the <c>CG04007</c> aborted error. Graph DESIGN.md, "Failed statements in explicit
/// transactions", records the contract and the reference engines it follows.
/// </para>
/// <para>
/// <b>Shape (concrete-types plan, phase 4, #1260).</b> A public sealed leaf of the root base with
/// an internal constructor; the session that began it is the only way to create one. It replaces
/// the model's internal transaction, which carried its own copy of the end state machine.
/// </para>
/// </remarks>
[global::System.Diagnostics.DebuggerDisplay("GraphDatabaseTransaction Id={Id}")]
[global::System.Diagnostics.DebuggerTypeProxy(typeof(global::Assimalign.Cohesion.Database.Graph.GraphDatabaseTransaction.DebugView))]
public sealed class GraphDatabaseTransaction : DatabaseTransaction
{
    // Aborted refusals are created on a normal path, so their messages are read once.
    private static readonly string _commitAborted = ErrorMessages.TransactionCommitAborted;
    private static readonly string _operationsAborted = ErrorMessages.TransactionOperationsAborted;
    private static readonly string _failureCause = ErrorMessages.TransactionFailureCause;

    private GraphMergeLocks? _mergeLocks;
    internal GraphMergeLocks MergeLocks => _mergeLocks ??= new();

    // The database whose offline state (#1243) refuses the transaction's commit and rollback and
    // makes its disposal touch nothing.
    private readonly GraphDatabase _database;

    internal GraphDatabaseTransaction(TransactionCoordinator coordinator, TransactionContext context, GraphDatabase database, DateTimeOffset transactionStart)
        : base(coordinator, context)
    {
        _database = database;
        TransactionStart = transactionStart;
    }

    /// <summary>
    /// Gets the MVCC transaction context the transaction's statements run under.
    /// </summary>
    internal TransactionContext Context => KernelContext;

    internal DateTimeOffset TransactionStart { get; }

    /// <summary>
    /// Gets whether statements may run in the transaction: the caller has not ended it, no
    /// statement aborted it, and its kernel transaction is active.
    /// </summary>
    internal bool AcceptsStatements => IsUsable;

    /// <summary>
    /// Admits one statement into the transaction, so a commit cannot start while it runs. Every
    /// admitted statement is paired with <see cref="EndStatement"/>.
    /// </summary>
    /// <returns>True when the transaction accepts the statement; false when it refuses statements.</returns>
    internal bool TryBeginStatement() => TryBeginOperation();

    /// <summary>Ends one statement <see cref="TryBeginStatement"/> admitted.</summary>
    internal void EndStatement() => EndOperation();

    /// <summary>
    /// Creates the session's refusal of a statement the transaction did not admit: the base's
    /// refusal, for the session.
    /// </summary>
    /// <returns>The refusal.</returns>
    internal DatabaseException CreateStatementRefusal() => CreateRefusal();

    /// <summary>
    /// Aborts the transaction because a statement failed in it (#1188): the base records the
    /// first failure as the cause and rolls the kernel transaction back, so it holds no writer
    /// lock while it waits for the caller's rollback.
    /// </summary>
    /// <param name="cause">The failure the caller observed from the statement.</param>
    /// <returns>A task that completes once the rollback ended.</returns>
    internal ValueTask AbortForFailedStatementAsync(Exception cause) => AbortAsync(cause);

    /// <inheritdoc />
    protected override TransactionState GetKernelState() => KernelContext.State;

    /// <inheritdoc />
    /// <remarks>
    /// While the database closes, the kernel refuses every end before it starts: a commit then
    /// fails with a <see cref="DatabaseException"/> carrying <c>CG09019</c> and the kernel's
    /// <see cref="ObjectDisposedException"/> cause, and the base reports the transaction ended
    /// with nothing committed. A commit whose record was written but could not be made durable
    /// throws <see cref="DatabaseTransactionCommitUnconfirmedException"/> and leaves the transaction
    /// <c>Committed</c>.
    /// </remarks>
    protected override async ValueTask CommitCoreAsync()
    {
        try
        {
            if (_database.BeforeTransactionEndAsync is { } beforeEnd) { await beforeEnd().ConfigureAwait(false); }
            // Like a rollback, a commit that started runs to completion: a cancellation here would
            // only turn into a kernel abort of work the caller asked to keep.
            await KernelCoordinator.CommitAsync(KernelContext).ConfigureAwait(false);
        }
        catch (Exception error)
        {
            // A kernel abort of the commit crosses the engine boundary as the area root's exception.
            var translated = Translate(error);
            if (ReferenceEquals(translated, error))
            {
                throw;
            }

            throw translated;
        }
        finally { _mergeLocks?.Dispose(); }
    }

    /// <inheritdoc />
    /// <remarks>
    /// The caller's token is not passed on (PostgreSQL holds interrupts through
    /// <c>AbortTransaction</c> for the same reason), and the kernel ends the context whatever
    /// fails once it starts (#1226). Only a closing database refuses the rollback before it starts,
    /// and its disposal then aborts the context itself. The refusal is a
    /// <see cref="DatabaseException"/> carrying <c>CG09019</c> and the kernel's
    /// <see cref="ObjectDisposedException"/> cause.
    /// </remarks>
    protected override async ValueTask RollbackCoreAsync()
    {
        try
        {
            if (_database.BeforeTransactionEndAsync is { } beforeEnd) { await beforeEnd().ConfigureAwait(false); }
            await KernelCoordinator.RollbackAsync(KernelContext).ConfigureAwait(false);
        }
        catch (Exception error)
        {
            var translated = Translate(error);
            if (ReferenceEquals(translated, error))
            {
                throw;
            }

            throw translated;
        }
        finally { _mergeLocks?.Dispose(); }
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
        // The cause travels in the message as well: the wire carries message text, not inner exceptions.
        var diagnostic = cause is DatabaseException failure && failure.Diagnostics.Count > 0 ? failure.Diagnostics[0] : null;
        string causeText = diagnostic?.Code is { } code && !cause!.Message.StartsWith(code + ":", StringComparison.Ordinal)
            ? $"{code}: {cause.Message}" : cause?.Message ?? string.Empty;
        // The transaction's own code lives in Diagnostics only; the wire writer adds the prefix
        // (Database DESIGN.md). The cause's code stays in the cause text, which is all the wire keeps of it.
        string reason = cause is null ? string.Empty : string.Format(CultureInfo.CurrentCulture, _failureCause, causeText);
        string message = (commit ? _commitAborted : _operationsAborted) + reason;
        return new DatabaseException(message, [new Types.Diagnostic
        { Code = "CG04007", Message = message, Severity = Types.DiagnosticSeverity.Error }], cause);
    }

    // A failure the offline storage caused is the database's coded refusal (#1243), checked first:
    // the offline error is a StorageException, which the kernel translation reports as CG06006.
    private Exception Translate(Exception error)
    {
        var offline = _database.TranslateOffline(error);
        return ReferenceEquals(offline, error) ? _database.TranslateKernelFailure(error) : offline;
    }

    private sealed class DebugView
    {
        private readonly global::Assimalign.Cohesion.Database.Graph.GraphDatabaseTransaction _target;

        public DebugView(global::Assimalign.Cohesion.Database.Graph.GraphDatabaseTransaction value)
        {
            _target = value;
        }

        public global::Assimalign.Cohesion.Database.Transactions.TransactionId Id => _target.Id;
        public global::Assimalign.Cohesion.Database.Transactions.IsolationLevel IsolationLevel => _target.IsolationLevel;
    }
}
