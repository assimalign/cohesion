using System;
using System.Threading.Tasks;

using Assimalign.Cohesion.Database.Transactions;

namespace Assimalign.Cohesion.Database.Documents;

/// <summary>
/// An explicit document transaction, over an MVCC transaction context from the database's
/// transaction coordinator. A statement that fails inside it (an OQL statement, or a collection or
/// document operation of the session) aborts the whole transaction (#1225, the contract #1188 set
/// for Graph): the engine rolls its work back at once, and the transaction stays the session's
/// transaction, reporting <see cref="TransactionState.Faulted"/>, until the caller ends it.
/// </summary>
/// <remarks>
/// <para>
/// <b>The end state machine is the root base's</b> (<see cref="DatabaseTransaction"/>, the
/// #1188, #1225 and #1226 contracts): until the caller ends an aborted transaction, the session
/// refuses every statement and BEGIN with <c>COHDBD001</c>; a rollback ends it, and a commit ends
/// it with <c>COHDBD001</c> and commits nothing, as every later commit does. A transaction that
/// did not commit accepts any number of rollbacks, a token is observed only before a commit or
/// rollback starts, and a commit is refused while a statement of the transaction still runs. A
/// commit or rollback that started always ends the context: the transaction kernel completes a
/// started rollback whatever fails or is canceled, and aborts a commit it cannot complete (#1226).
/// This type supplies the model's vocabulary only: the kernel state, the kernel commit and
/// rollback with the engine's exception translation, the offline refusal (<c>COHDBD002</c>,
/// #1243) and the <c>COHDBD001</c> aborted error. Documents DESIGN.md, "Failed statements in
/// explicit transactions", records the contract and the reference engines it follows.
/// </para>
/// <para>
/// <b>Shape (concrete-types plan, phase 4, #1260).</b> A public sealed leaf of the root base with
/// an internal constructor; the session that began it is the only way to create one. It replaces
/// the model's internal transaction, which carried its own copy of the end state machine.
/// </para>
/// </remarks>
public sealed class DocumentDatabaseTransaction : DatabaseTransaction
{
    private readonly TransactionCoordinator _coordinator;
    private readonly TransactionContext _context;

    // The database whose offline state (#1243) refuses the transaction's commit and rollback and
    // makes its disposal touch nothing.
    private readonly DocumentDatabase _database;

    internal DocumentDatabaseTransaction(TransactionCoordinator coordinator, TransactionContext context, DocumentDatabase database)
        : base(context.Id, context.IsolationLevel)
    {
        _coordinator = coordinator;
        _context = context;
        _database = database;
    }

    /// <summary>
    /// Gets the MVCC transaction context the transaction's statements run under.
    /// </summary>
    internal TransactionContext Context => _context;

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
    /// Aborts the transaction because a statement failed in it (#1225): the base records the
    /// first failure as the cause and rolls the kernel transaction back, so it holds no writer
    /// lock while it waits for the caller's rollback.
    /// </summary>
    /// <param name="cause">The failure the caller observed from the statement.</param>
    /// <returns>A task that completes once the rollback ended.</returns>
    internal ValueTask AbortForFailedStatementAsync(Exception cause) => AbortAsync(cause);

    /// <inheritdoc />
    protected override TransactionState GetKernelState() => _context.State;

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
            await _coordinator.CommitAsync(_context).ConfigureAwait(false);
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
            await _coordinator.RollbackAsync(_context).ConfigureAwait(false);
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
    }

    /// <inheritdoc />
    protected override DatabaseException? GetOfflineRefusal() => _database.GetOfflineRefusal();

    /// <inheritdoc />
    protected override DatabaseException CreateAbortedException(Exception? cause, bool commit)
    {
        // The cause travels in the message as well, so it survives any boundary that keeps only text.
        string reason = cause is null ? string.Empty : " Cause: " + cause.Message;
        string message = commit
            ? "COHDBD001: The session's transaction is aborted and cannot commit; nothing was committed." + reason
            : "COHDBD001: The session's transaction is aborted; statements are refused until it is rolled back." + reason;
        return new DatabaseException(message, cause);
    }

    // A failure the offline storage caused is the database's coded refusal (#1243); any other
    // kernel failure crosses the boundary as the area root's exception.
    private Exception Translate(Exception error)
    {
        var offline = _database.TranslateOffline(error);
        return ReferenceEquals(offline, error) ? DocumentDatabase.TranslateKernelFailure(error) : offline;
    }
}
