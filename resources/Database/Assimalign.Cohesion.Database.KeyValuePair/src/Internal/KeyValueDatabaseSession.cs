using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Database.KeyValuePair.Internal;

using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.Transactions;

/// <summary>
/// Internal implementation of a key-value database session, bound to the
/// database's MVCC transaction manager: explicit and auto-commit commands alike
/// run under an <see cref="ITransactionContext"/> paired with a storage bracket,
/// so visibility semantics never fork between the two paths.
/// </summary>
internal sealed class KeyValueDatabaseSession : IDatabaseSession
{
    private readonly TransactionCoordinator _coordinator;
    private readonly KeyValueOperationExecutor _executor;

    private KeyValueDatabaseTransaction? _transaction;
    private SessionState _state;

    internal KeyValueDatabaseSession(IKeyValueDatabase database, TransactionCoordinator coordinator, KeyValueOperationExecutor executor)
    {
        Database = database;
        _coordinator = coordinator;
        _executor = executor;
        _state = SessionState.Open;
    }

    /// <inheritdoc />
    public IDatabase Database { get; }

    /// <inheritdoc />
    public SessionState State => _state;

    /// <inheritdoc />
    /// <remarks>
    /// The transaction stays current until the caller commits, rolls back or disposes it, including
    /// one the kernel ended under its caller (<see cref="TransactionState.Faulted"/>), which waits
    /// for the caller's rollback. A failed command never ends it.
    /// </remarks>
    public IDatabaseTransaction? CurrentTransaction => OpenTransaction;

    private KeyValueDatabaseTransaction? OpenTransaction => _transaction is { IsOpen: true } transaction ? transaction : null;

    /// <inheritdoc />
    public ValueTask<IDatabaseTransaction> BeginTransactionAsync(CancellationToken cancellationToken = default)
        => BeginTransactionAsync(IsolationLevel.Snapshot, cancellationToken);

    /// <inheritdoc />
    /// <remarks>
    /// The session begins an MVCC transaction context on the database's
    /// transaction manager alongside the physical storage bracket (paired under
    /// one sequence): <see cref="IsolationLevel.Snapshot"/> fixes the visibility
    /// snapshot at begin, <see cref="IsolationLevel.ReadCommitted"/> refreshes
    /// it per command. <see cref="IsolationLevel.Serializable"/> is rejected —
    /// the engine has no serialization-conflict detection yet, and the root
    /// contract forbids running a transaction weaker than requested.
    /// </remarks>
    public async ValueTask<IDatabaseTransaction> BeginTransactionAsync(IsolationLevel isolationLevel, CancellationToken cancellationToken = default)
    {
        ThrowIfNotOpen();
        cancellationToken.ThrowIfCancellationRequested();

        if (isolationLevel == IsolationLevel.Serializable)
        {
            throw new DatabaseException(
                "IsolationLevel.Serializable is not supported by the key-value engine yet: serialization-conflict " +
                "detection is a post-MVP feature, and the session contract forbids running weaker than requested. " +
                "Use IsolationLevel.Snapshot or IsolationLevel.ReadCommitted.");
        }

        if (OpenTransaction is { } open)
        {
            // BEGIN is refused while a transaction the kernel ended under its caller waits for its
            // rollback, as in PostgreSQL's failed transaction block.
            throw open.IsUsable
                ? new DatabaseException("A transaction is already active on this session.")
                : open.CreateRefusal();
        }

        var context = await _coordinator.BeginAsync(isolationLevel, cancellationToken).ConfigureAwait(false);
        _transaction = new KeyValueDatabaseTransaction(_coordinator, context);

        return _transaction;
    }

    /// <inheritdoc />
    public async ValueTask<QueryResult> ExecuteAsync(QueryRequest request, CancellationToken cancellationToken = default)
    {
        ThrowIfNotOpen();
        ArgumentNullException.ThrowIfNull(request);

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
        if (OpenTransaction is { } transaction)
        {
            // The admission also keeps a commit from starting while the command runs. A rollback
            // may still end the transaction underneath it (a host's rollback of a wire session's
            // transaction): the command then fails, and the kernel applies nothing for it.
            if (!transaction.TryBeginCommand())
            {
                throw transaction.CreateRefusal();
            }

            var scope = new KeyValueStatementContext(transaction.Context, _coordinator);

            try
            {
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
                transaction.EndCommand();
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
            throw new DatabaseTransactionCommitUnconfirmedException(exception.Message, exception);
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

    /// <inheritdoc />
    /// <remarks>
    /// The model-agnostic text-execute seam: key-value sessions parse the command
    /// grammar (<c>docs/COMMANDS.md</c> — <c>GET</c>/<c>PUT</c>/<c>DELETE</c>/
    /// <c>EXISTS</c>/<c>SCAN</c> with parameter operands, and <c>KEYSPACES</c>) into the same typed
    /// requests the typed seam executes — this is what lets the wire-protocol
    /// server execute key-value commands through the existing Execute message
    /// with zero protocol changes.
    /// </remarks>
    public ValueTask<QueryResult> ExecuteAsync(string statement, IReadOnlyDictionary<string, object?>? parameters = null, CancellationToken cancellationToken = default)
    {
        ThrowIfNotOpen();
        ArgumentException.ThrowIfNullOrWhiteSpace(statement);

        // A transaction that refuses commands refuses the text before it is parsed.
        if (OpenTransaction is { IsUsable: false } refusing)
        {
            throw refusing.CreateRefusal();
        }

        return ExecuteAsync(KeyValueCommandParser.Parse(statement, parameters), cancellationToken);
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_state == SessionState.Closed)
        {
            return;
        }

        _state = SessionState.Closed;

        // Roll back the transaction the caller left open, including one whose end did not
        // complete. The transaction object stays with its caller, whose later rollback is a no-op
        // and whose later commit fails with COHDBK001 naming the closure.
        if (OpenTransaction is { } transaction)
        {
            await transaction.CloseAsync(new DatabaseException("The key-value session closed before the transaction ended.")).ConfigureAwait(false);
        }

        _transaction = null;
    }

    private void ThrowIfNotOpen()
    {
        if (_state != SessionState.Open)
        {
            throw new DatabaseException($"Session is not open. Current state: {_state}.");
        }
    }
}
