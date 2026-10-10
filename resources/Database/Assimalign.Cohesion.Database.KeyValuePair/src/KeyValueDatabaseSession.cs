using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Database.KeyValuePair;

using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.KeyValuePair.Internal;
using Assimalign.Cohesion.Database.Transactions;

/// <summary>
/// A key-value database session, bound to the database's MVCC transaction manager: explicit and
/// auto-commit commands alike run under a <see cref="TransactionContext"/> paired with a storage
/// bracket, so visibility semantics never fork between the two paths.
/// </summary>
/// <remarks>
/// <para>
/// <b>The session's state, its explicit transaction and the "already active" check are the root
/// base's</b> (<see cref="DatabaseSession"/>, §6.4 of the concrete-types plan): BEGIN is refused
/// with "A transaction or operation is already active on this session." while the session's
/// transaction is usable, a closed session refuses everything with "The session is closed.", and
/// disposal rolls the open transaction back as the session's teardown. This type supplies the
/// model's work: the isolation-level and offline refusals of BEGIN, the commands, and the
/// translation of the kernel's exceptions at the model boundary.
/// </para>
/// <para>
/// <b>A command is statement-atomic.</b> Inside an explicit transaction, a command that fails
/// writes nothing and leaves the transaction active, as a failed SQL statement does; the session
/// never aborts its transaction for a failed command (DESIGN.md, "Failed commands in explicit
/// transactions"). Commands do not hold the session (the base's operation hold): an auto-commit
/// command and a BEGIN may run side by side, as before the bases.
/// </para>
/// <para>
/// <b>Shape (concrete-types plan, phase 4, #1260).</b> A public sealed leaf of the root base with
/// an internal constructor; <see cref="KeyValueDatabase.CreateSessionAsync"/> creates it. The
/// database and the transaction are re-exposed typed with <c>new</c> members over the base's
/// public members.
/// </para>
/// </remarks>
public sealed class KeyValueDatabaseSession : DatabaseSession
{
    private readonly KeyValueDatabase _database;
    private readonly TransactionCoordinator _coordinator;
    private readonly KeyValueOperationExecutor _executor;

    internal KeyValueDatabaseSession(KeyValueDatabase database, TransactionCoordinator coordinator, KeyValueOperationExecutor executor)
        : base(database)
    {
        _database = database;
        _coordinator = coordinator;
        _executor = executor;
    }

    /// <summary>
    /// Gets the key-value database this session is scoped to.
    /// </summary>
    public new KeyValueDatabase Database => _database;

    /// <summary>
    /// Gets the session's explicit transaction until the caller ends it, including one the kernel
    /// ended under its caller (<see cref="TransactionState.Faulted"/>), which waits for the caller's
    /// rollback; null when none is open. A failed command never ends it.
    /// </summary>
    public new KeyValueDatabaseTransaction? CurrentTransaction => (KeyValueDatabaseTransaction?)base.CurrentTransaction;

    /// <summary>
    /// Begins an explicit transaction at the default isolation level, <see cref="IsolationLevel.Snapshot"/>.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token for the operation.</param>
    /// <returns>The new transaction, now the session's transaction.</returns>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was canceled before the transaction began.</exception>
    /// <exception cref="DatabaseException">
    /// The session is closed; a transaction or operation is already active on it; or the session's
    /// transaction refuses work (<c>COHDBK001</c>).
    /// </exception>
    /// <exception cref="DatabaseOfflineException">The database is offline (<c>COHDBK002</c>, #1243).</exception>
    public new async ValueTask<KeyValueDatabaseTransaction> BeginTransactionAsync(CancellationToken cancellationToken = default)
        => (KeyValueDatabaseTransaction)await base.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

    /// <summary>
    /// Begins an explicit transaction at the requested isolation level.
    /// </summary>
    /// <param name="isolationLevel">The isolation level the transaction executes under.</param>
    /// <param name="cancellationToken">Cancellation token for the operation.</param>
    /// <returns>The new transaction, now the session's transaction.</returns>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was canceled before the transaction began.</exception>
    /// <exception cref="DatabaseException">
    /// The session is closed; a transaction or operation is already active on it; the session's
    /// transaction refuses work (<c>COHDBK001</c>); or <paramref name="isolationLevel"/> is
    /// <see cref="IsolationLevel.Serializable"/>.
    /// </exception>
    /// <exception cref="DatabaseOfflineException">The database is offline (<c>COHDBK002</c>, #1243).</exception>
    /// <remarks>
    /// The session begins an MVCC transaction context on the database's transaction manager
    /// alongside the physical storage bracket (paired under one sequence):
    /// <see cref="IsolationLevel.Snapshot"/> fixes the visibility snapshot at begin,
    /// <see cref="IsolationLevel.ReadCommitted"/> refreshes it per command.
    /// <see cref="IsolationLevel.Serializable"/> is rejected: the engine has no
    /// serialization-conflict detection yet, and the root contract forbids running a transaction
    /// weaker than requested. The base refuses a closed session and an active transaction before
    /// the isolation level and the offline database are checked.
    /// </remarks>
    public new async ValueTask<KeyValueDatabaseTransaction> BeginTransactionAsync(IsolationLevel isolationLevel, CancellationToken cancellationToken = default)
        => (KeyValueDatabaseTransaction)await base.BeginTransactionAsync(isolationLevel, cancellationToken).ConfigureAwait(false);

    /// <inheritdoc />
    protected override async ValueTask<DatabaseTransaction> BeginTransactionCoreAsync(IsolationLevel isolationLevel, CancellationToken cancellationToken)
    {
        _database.ThrowIfOffline();
        if (isolationLevel == IsolationLevel.Serializable)
        {
            throw new DatabaseException(
                "IsolationLevel.Serializable is not supported by the key-value engine yet: serialization-conflict " +
                "detection is a post-MVP feature, and the session contract forbids running weaker than requested. " +
                "Use IsolationLevel.Snapshot or IsolationLevel.ReadCommitted.");
        }

        TransactionContext context;
        try
        {
            context = await _coordinator.BeginAsync(isolationLevel, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (_database.TranslateOffline(exception) is DatabaseOfflineException offline)
        {
            throw offline;
        }

        return new KeyValueDatabaseTransaction(_coordinator, context, _database);
    }

    /// <inheritdoc />
    protected override ValueTask<QueryResult> ExecuteCoreAsync(QueryRequest request, CancellationToken cancellationToken)
        => ExecuteRequestAsync(request, cancellationToken);

    /// <inheritdoc />
    /// <remarks>
    /// The model-agnostic text-execute seam: key-value sessions parse the command grammar
    /// (<c>docs/COMMANDS.md</c>: <c>GET</c>/<c>PUT</c>/<c>DELETE</c>/<c>EXISTS</c>/<c>SCAN</c> with
    /// parameter operands, and <c>KEYSPACES</c>) into the same typed requests the typed seam
    /// executes. This is what lets the wire-protocol server execute key-value commands through the
    /// existing Execute message with zero protocol changes.
    /// </remarks>
    protected override ValueTask<QueryResult> ExecuteCoreAsync(string statement, IReadOnlyDictionary<string, object?>? parameters, CancellationToken cancellationToken)
    {
        _database.ThrowIfOffline();

        // A transaction that refuses commands refuses the text before it is parsed.
        ThrowIfTransactionRefuses();
        return ExecuteRequestAsync(KeyValueCommandParser.Parse(statement, parameters), cancellationToken);
    }

    private async ValueTask<QueryResult> ExecuteRequestAsync(QueryRequest request, CancellationToken cancellationToken)
    {
        _database.ThrowIfOffline();

        try
        {
            return await ExecuteCommandAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (_database.TranslateOffline(exception) is var translated
            && !ReferenceEquals(translated, exception))
        {
            // A command that met the offline storage (#1243) gets the coded refusal, or is
            // unconfirmed when a storage commit record was written before the flush failed; the
            // unconfirmed commit that took it offline keeps its own type.
            throw translated;
        }
    }

    private async ValueTask<QueryResult> ExecuteCommandAsync(QueryRequest request, CancellationToken cancellationToken)
    {
        if (request is not KeyValueRequest command)
        {
            throw new DatabaseException(
                $"The key-value session executes {nameof(KeyValueRequest)} commands; {request.GetType().Name} is not one.");
        }

        // Inside an explicit transaction, the command rides its context. A command is
        // statement-atomic: its writes share one physical bracket that a failure rolls back, so a
        // failed command writes nothing and the transaction stays active, as a failed SQL
        // statement does. Only a transaction that is ending, or that the kernel ended under its
        // caller, refuses commands, so a command never runs in a half-rolled-back transaction or
        // silently autocommits (#1225).
        if (CurrentTransaction is { } transaction)
        {
            // The admission also keeps a commit from starting while the command runs. A rollback
            // may still end the transaction underneath it (a host's rollback of a wire session's
            // transaction): the command then fails, and the kernel applies nothing for it.
            if (!transaction.TryBeginCommand())
            {
                throw transaction.CreateCommandRefusal();
            }

            TransactionContext? snapshotPin = null;

            try
            {
                snapshotPin = await BeginSnapshotPinAsync(transaction.Context, cancellationToken).ConfigureAwait(false);

                // The scope captures the command's snapshot after the pin began, so the pin's floor
                // is at or below the command's (#1363). It runs under the transaction's own context,
                // as the Sql session's statements do.
                var scope = new KeyValueStatementContext(transaction.Context, _coordinator);
                return await _executor.ExecuteAsync(command, scope, cancellationToken).ConfigureAwait(false);
            }
            catch (TransactionDeadlockException exception)
            {
                // The requester-closes-cycle victim: the command failed and is
                // retryable by construction. The session stays usable.
                throw new DatabaseTransactionDeadlockException(exception.Message, exception);
            }
            catch (TransactionAbortedException exception)
            {
                throw new DatabaseTransactionAbortedException(exception.Message, exception);
            }
            finally
            {
                try
                {
                    await ReleaseSnapshotPinAsync(snapshotPin).ConfigureAwait(false);
                }
                finally
                {
                    transaction.EndCommand();
                }
            }
        }

        // Auto-commit semantics: a one-command manager transaction, so
        // visibility and conflict semantics are identical to the explicit path.
        var context = await _coordinator.BeginAsync(IsolationLevel.Snapshot, cancellationToken).ConfigureAwait(false);

        try
        {
            var scope = new KeyValueStatementContext(context, _coordinator);
            var result = await _executor.ExecuteAsync(command, scope, cancellationToken).ConfigureAwait(false);
            await _coordinator.CommitAsync(context, cancellationToken).ConfigureAwait(false);
            return result;
        }
        catch (TransactionCommitUnconfirmedException exception)
        {
            // The command committed; only the durability of its commit record is unconfirmed.
            throw _database.CreateUnconfirmedCommit(exception);
        }
        catch (TransactionDeadlockException exception)
        {
            if (context.State == TransactionState.Active)
            {
                await _coordinator.RollbackAsync(context, CancellationToken.None).ConfigureAwait(false);
            }

            throw new DatabaseTransactionDeadlockException(exception.Message, exception);
        }
        catch (TransactionAbortedException exception)
        {
            if (context.State == TransactionState.Active)
            {
                await _coordinator.RollbackAsync(context, CancellationToken.None).ConfigureAwait(false);
            }

            throw new DatabaseTransactionAbortedException(exception.Message, exception);
        }
        catch
        {
            if (context.State == TransactionState.Active)
            {
                await _coordinator.RollbackAsync(context, CancellationToken.None).ConfigureAwait(false);
            }

            throw;
        }
    }

    /// <summary>
    /// Begins the snapshot pin of a command in a <see cref="IsolationLevel.ReadCommitted"/>
    /// transaction: a snapshot transaction of its own, begun before the command captures its
    /// snapshot, as the Documents, Graph and Blob operations begin theirs.
    /// </summary>
    /// <param name="transaction">The explicit transaction's context.</param>
    /// <param name="cancellationToken">Observed by the begin.</param>
    /// <returns>The pin, or null when the transaction's snapshot is fixed at its begin.</returns>
    /// <remarks>
    /// The version purge reclaims below the transaction manager's prune bound, to which a
    /// read-committed transaction adds only its own sequence: its snapshot is captured afresh on
    /// every access. The command's snapshot keeps the floor of the moment it was captured, which
    /// can be lower, because a writer that began before this transaction was still in flight then.
    /// Once that writer commits, nothing but this pin keeps the purge from reclaiming the versions
    /// it tombstoned while the command still reads them, which would drop those keys from a scan or
    /// read a replaced key as absent. The pin's snapshot is captured first, so its floor is at or
    /// below the command's (#1363). The pin only holds the bound: the command still runs under the
    /// transaction's own context, as a Sql statement does.
    /// </remarks>
    private async ValueTask<TransactionContext?> BeginSnapshotPinAsync(TransactionContext transaction, CancellationToken cancellationToken)
        => transaction.IsolationLevel == IsolationLevel.ReadCommitted
            ? await _coordinator.BeginAsync(IsolationLevel.Snapshot, cancellationToken).ConfigureAwait(false)
            : null;

    /// <summary>
    /// Ends a command's snapshot pin, on every path out of the command. It always ends rolled
    /// back, with no token: it wrote nothing. On an offline database it touches nothing (#1243),
    /// and the reopen's recovery aborts it.
    /// </summary>
    /// <param name="snapshotPin">The pin, or null when the command has none.</param>
    private async ValueTask ReleaseSnapshotPinAsync(TransactionContext? snapshotPin)
    {
        if (snapshotPin?.State == TransactionState.Active && !_database.IsOffline)
        {
            await _coordinator.RollbackAsync(snapshotPin, CancellationToken.None).ConfigureAwait(false);
        }
    }
}
