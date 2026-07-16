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
    private readonly KeyValueTransactionCoordinator _coordinator;
    private readonly KeyValueOperationExecutor _executor;

    private KeyValueDatabaseTransaction? _transaction;
    private SessionState _state;

    internal KeyValueDatabaseSession(IKeyValueDatabase database, KeyValueTransactionCoordinator coordinator, KeyValueOperationExecutor executor)
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
    public IDatabaseTransaction? CurrentTransaction => _transaction;

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

        if (_transaction is not null && _transaction.State == TransactionState.Active)
        {
            throw new DatabaseException("A transaction is already active on this session.");
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

        // Inside an explicit transaction, the command rides its context.
        if (_transaction is not null && _transaction.State == TransactionState.Active)
        {
            var scope = new KeyValueStatementContext(_transaction.Context, _coordinator);

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
    /// <c>EXISTS</c>/<c>SCAN</c> with parameter operands) into the same typed
    /// requests the typed seam executes — this is what lets the wire-protocol
    /// server execute key-value commands through the existing Execute message
    /// with zero protocol changes.
    /// </remarks>
    public ValueTask<QueryResult> ExecuteAsync(string statement, IReadOnlyDictionary<string, object?>? parameters = null, CancellationToken cancellationToken = default)
    {
        ThrowIfNotOpen();
        ArgumentException.ThrowIfNullOrWhiteSpace(statement);

        return ExecuteAsync(KeyValueCommandParser.Parse(statement, parameters), cancellationToken);
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_state == SessionState.Closed)
        {
            return;
        }

        // Auto-rollback any active transaction
        if (_transaction is not null && _transaction.State == TransactionState.Active)
        {
            await _transaction.DisposeAsync().ConfigureAwait(false);
        }

        _transaction = null;
        _state = SessionState.Closed;
    }

    private void ThrowIfNotOpen()
    {
        if (_state != SessionState.Open)
        {
            throw new DatabaseException($"Session is not open. Current state: {_state}.");
        }
    }
}
