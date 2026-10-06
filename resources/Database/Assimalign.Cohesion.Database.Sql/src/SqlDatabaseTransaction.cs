using System;
using System.Threading.Tasks;

using Assimalign.Cohesion.Database.Sql.Catalog;
using Assimalign.Cohesion.Database.Transactions;

namespace Assimalign.Cohesion.Database.Sql;

/// <summary>
/// An explicit ACID transaction of a SQL session, over an MVCC transaction context from the
/// database's transaction manager (the engine owns both vocabularies; this is the translation
/// boundary). Commit and rollback flow through the manager, whose journal-bound log owns the commit
/// record and its durability await; rollback undoes the writer's stamps through the version
/// store's ledger and releases its locks.
/// </summary>
/// <remarks>
/// <para>
/// <b>The end state machine is the root base's</b> (<see cref="DatabaseTransaction"/>, the #1188
/// contract): commit, rollback, disposal and the session's teardown pass one end gate, so they
/// never race into the kernel; a transaction that did not commit accepts any number of rollbacks;
/// a token is observed only before a commit or rollback starts; a commit while a statement of the
/// transaction still runs is refused and leaves it active; and a transaction the kernel ended under
/// its caller (its database was dropped or closed, say) reports <see cref="TransactionState.Faulted"/>
/// and refuses statements, BEGIN and COMMIT with <c>COHSQLT005</c> until the caller rolls it back.
/// This type supplies the model's vocabulary only: the kernel state, the kernel commit and rollback
/// with the engine's exception translation, the offline refusal (<c>COHSQLT004</c>, #1243) and the
/// <c>COHSQLT005</c> aborted error.
/// </para>
/// <para>
/// <b>A statement is statement-atomic</b>, so a failed statement leaves the transaction active (a
/// constraint violation, an evaluation fault, a deadlock victim): the SQL model never aborts its
/// transaction for a failed statement, the owner's 2026-10-04 per-statement decision, and never
/// calls the base's abort. A commit or rollback that started always ends the context (#1226): the
/// kernel completes a started rollback whatever fails, keeping the locks of a writer whose undo it
/// must defer, and aborts a commit it cannot complete. A rollback ends the transaction even under a
/// running statement, which then fails and applies nothing.
/// </para>
/// <para>
/// <b>Shape (concrete-types plan, phase 4, #1260).</b> A public sealed leaf of the root base with
/// an internal constructor; the session that began it is the only way to create one.
/// </para>
/// </remarks>
public sealed class SqlDatabaseTransaction : DatabaseTransaction
{
    /// <summary>
    /// The code that leads the message of work refused because the session's transaction is
    /// aborted: the kernel ended it under its caller, or the session's teardown ended it.
    /// </summary>
    internal const string AbortedCode = "COHSQLT005";

    private readonly TransactionCoordinator _coordinator;
    private readonly TransactionContext _context;

    // The database whose offline state (#1243) refuses the transaction's commit and rollback and
    // makes its disposal touch nothing.
    private readonly SqlDatabase _database;
    private readonly SqlCatalogSnapshot? _catalogSnapshot;

    internal SqlDatabaseTransaction(TransactionCoordinator coordinator, TransactionContext context, SqlDatabase database, SqlCatalogSnapshot? catalogSnapshot)
        : base(context.Id, context.IsolationLevel)
    {
        _coordinator = coordinator;
        _context = context;
        _database = database;
        _catalogSnapshot = catalogSnapshot;
    }

    /// <summary>
    /// Gets the MVCC transaction context statements execute under: the executor stamps writes with
    /// its sequence and resolves reads through its snapshot (re-captured per statement under
    /// <see cref="IsolationLevel.ReadCommitted"/>, fixed at begin under
    /// <see cref="IsolationLevel.Snapshot"/>).
    /// </summary>
    internal TransactionContext Context => _context;

    /// <summary>
    /// Gets the catalog capture a <see cref="IsolationLevel.Snapshot"/> transaction took at BEGIN,
    /// which every system-view statement of the transaction reads; null for
    /// <see cref="IsolationLevel.ReadCommitted"/>, whose statements capture at their start.
    /// </summary>
    internal SqlCatalogSnapshot? CatalogSnapshot => _catalogSnapshot;

    /// <summary>Gets the number of admitted statements still running (observability for tests and diagnostics).</summary>
    internal int RunningStatements => RunningOperations;

    /// <summary>
    /// Admits one statement into the transaction, so a commit cannot start while it runs. Every
    /// admitted statement is paired with <see cref="EndStatement"/>.
    /// </summary>
    /// <returns>True when the transaction accepts the statement; false when it refuses statements.</returns>
    internal bool TryBeginStatement() => TryBeginOperation();

    /// <summary>Ends one statement admitted by <see cref="TryBeginStatement"/>.</summary>
    internal void EndStatement() => EndOperation();

    /// <summary>
    /// Creates the session's refusal of a statement the transaction did not admit
    /// (<see cref="TryBeginStatement"/> returned false): the base's refusal, for the session.
    /// </summary>
    /// <returns>The refusal.</returns>
    internal DatabaseException CreateStatementRefusal() => CreateRefusal();

    /// <inheritdoc />
    protected override TransactionState GetKernelState() => _context.State;

    /// <inheritdoc />
    /// <remarks>
    /// The caller's token is not passed on: under the base a token is observed only before the
    /// commit starts. While the database closes, the kernel refuses every end before it starts: a
    /// commit then fails with <see cref="ObjectDisposedException"/>, and the base reports the
    /// transaction ended with nothing committed. A commit whose record was written but could not be
    /// made durable throws <see cref="DatabaseTransactionCommitUnconfirmedException"/> and leaves the
    /// transaction <c>Committed</c>: the reopen's recovery decides whether it survives (#1243), and
    /// the work must not be retried.
    /// </remarks>
    protected override async ValueTask CommitCoreAsync()
    {
        try
        {
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
    /// <c>AbortTransaction</c> for the same reason), and the kernel ends the context whatever fails
    /// once it starts (#1226). Only a closing database refuses the rollback before it starts, and
    /// its disposal then aborts the context itself.
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
        // The cause travels in the message as well: the wire carries message text, not inner exceptions.
        string reason = cause is null ? string.Empty : " Cause: " + cause.Message;
        string message = commit
            ? AbortedCode + ": The session's transaction is aborted and cannot commit; nothing was committed." + reason
            : AbortedCode + ": The session's transaction is aborted; statements are refused until it is rolled back." + reason;
        return new DatabaseException(message, cause);
    }

    // The area error policy: the engine translates the transaction kernel's independent exception
    // root at the model boundary. A failure the offline storage caused becomes the coded refusal
    // (#1243); the unconfirmed commit that took it offline keeps its own type.
    private Exception Translate(Exception error) => error switch
    {
        TransactionCommitUnconfirmedException => new DatabaseTransactionCommitUnconfirmedException(error.Message, error),
        _ when _database.TranslateOffline(error) is var offline && !ReferenceEquals(offline, error) => offline,
        TransactionDeadlockException => new DatabaseTransactionDeadlockException(error.Message, error),
        TransactionAbortedException => new DatabaseTransactionAbortedException(error.Message, error),
        _ => error,
    };
}
