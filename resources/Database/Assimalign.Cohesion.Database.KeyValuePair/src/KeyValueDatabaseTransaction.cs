using System;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Database.KeyValuePair;

using Assimalign.Cohesion.Database.Transactions;

/// <summary>
/// An explicit ACID transaction of a key-value session, over an MVCC transaction context from
/// the database's transaction manager. Commit and rollback flow through the manager, whose
/// journal-bound log owns the commit record and its durability await; rollback undoes the
/// writer's stamps through the version store's ledger and releases its key locks.
/// </summary>
/// <remarks>
/// <para>
/// <b>The end state machine is the root base's</b> (<see cref="DatabaseTransaction"/>, the #1188
/// contract): a transaction that did not commit accepts any number of rollbacks, a token is
/// observed only before a commit or rollback starts, a commit while a command of the transaction
/// still runs is refused and leaves it active, and a transaction the kernel ended under its caller
/// reports <see cref="TransactionState.Faulted"/> and refuses commands, BEGIN and COMMIT with
/// <c>COHDBK001</c> until the caller ends it. This type supplies the model's vocabulary only: the
/// kernel state, the kernel commit and rollback with the engine's exception translation, the
/// offline refusal (<c>COHDBK002</c>, #1243) and the <c>COHDBK001</c> aborted error.
/// </para>
/// <para>
/// <b>A command is statement-atomic</b>, so a failed command leaves the transaction active
/// (DESIGN.md, "Failed commands in explicit transactions"): the key-value model never aborts its
/// transaction for a failed command, the per-command rule of the owner's 2026-10-04 decision. A
/// commit or rollback that started always ends the context (#1226): the kernel completes a started
/// rollback whatever fails, keeping the key locks of a writer whose undo it must defer, and aborts
/// a commit it cannot complete. A rollback ends the transaction even under a running command,
/// which then fails and writes nothing.
/// </para>
/// <para>
/// <b>Shape (concrete-types plan, phase 4, #1260).</b> A public sealed leaf of the root base with
/// an internal constructor; the session that began it is the only way to create one.
/// </para>
/// </remarks>
public sealed class KeyValueDatabaseTransaction : DatabaseTransaction
{
    private readonly TransactionCoordinator _coordinator;
    private readonly TransactionContext _context;

    // The database whose offline state (#1243) refuses the transaction's commit and rollback and
    // makes its disposal touch nothing.
    private readonly KeyValueDatabase _database;

    internal KeyValueDatabaseTransaction(TransactionCoordinator coordinator, TransactionContext context, KeyValueDatabase database)
        : base(context.Id, context.IsolationLevel)
    {
        _coordinator = coordinator;
        _context = context;
        _database = database;
    }

    /// <summary>
    /// Gets the MVCC transaction context commands execute under: the executor stamps writes with
    /// its sequence and resolves reads through its snapshot (re-captured per command under
    /// <see cref="IsolationLevel.ReadCommitted"/>, fixed at begin under
    /// <see cref="IsolationLevel.Snapshot"/>).
    /// </summary>
    internal TransactionContext Context => _context;

    /// <summary>Gets the number of admitted commands still running (observability for tests and diagnostics).</summary>
    internal int RunningCommands => RunningOperations;

    /// <summary>
    /// Admits one command into the transaction, so a commit cannot start while it runs. Every
    /// admitted command is paired with <see cref="EndCommand"/>.
    /// </summary>
    /// <returns>True when the transaction accepts the command; false when it refuses commands.</returns>
    internal bool TryBeginCommand() => TryBeginOperation();

    /// <summary>Ends one command admitted by <see cref="TryBeginCommand"/>.</summary>
    internal void EndCommand() => EndOperation();

    /// <summary>
    /// Creates the session's refusal of a command the transaction did not admit
    /// (<see cref="TryBeginCommand"/> returned false): the base's refusal, for the session.
    /// </summary>
    /// <returns>The refusal.</returns>
    internal DatabaseException CreateCommandRefusal() => CreateRefusal();

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
        // The cause travels in the message as well: the wire carries message text, not inner exceptions.
        string reason = cause is null ? string.Empty : " Cause: " + cause.Message;
        string message = commit
            ? "COHDBK001: The session's transaction is aborted and cannot commit; nothing was committed." + reason
            : "COHDBK001: The session's transaction is aborted; commands are refused until it is rolled back." + reason;
        return new DatabaseException(message, cause);
    }

    // The area error policy: the engine translates the transaction kernel's independent exception
    // root at the model boundary. A failure the offline storage caused becomes the coded refusal
    // (#1243); the unconfirmed commit that took it offline keeps its own type, its message led by
    // the offline code (#1272).
    private Exception Translate(Exception error) => error switch
    {
        TransactionCommitUnconfirmedException unconfirmed => _database.CreateUnconfirmedCommit(unconfirmed),
        _ when _database.TranslateOffline(error) is var offline && !ReferenceEquals(offline, error) => offline,
        TransactionDeadlockException => new DatabaseTransactionDeadlockException(error.Message, error),
        TransactionAbortedException => new DatabaseTransactionAbortedException(error.Message, error),
        _ => error,
    };
}
