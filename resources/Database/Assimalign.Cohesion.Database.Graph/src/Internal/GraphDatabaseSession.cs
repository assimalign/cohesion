using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.Graph.Language;
using Assimalign.Cohesion.Database.Transactions;

namespace Assimalign.Cohesion.Database.Graph.Internal;

/// <summary>
/// A graph session. Its explicit transaction stays the session's transaction until the caller
/// commits, rolls back or disposes it; a statement that fails inside it aborts it, and the session
/// then refuses every statement and BEGIN with <c>COHDBG007</c> until the caller rolls back. A
/// failure never turns later statements into autocommit writes (#1188).
/// </summary>
internal sealed class GraphDatabaseSession : IDatabaseSession
{
    private readonly GraphDatabaseInstance _database;
    private readonly HashSet<GraphOperation> _operations = [];
    private readonly object _sync = new();
    private bool _reserved;
    private GraphDatabaseTransaction? _transaction;
    internal GraphDatabaseSession(GraphDatabaseInstance database)
    {
        _database = database;
        Database = database;
    }
    public IDatabase Database { get; }
    internal GraphDatabaseInstance Instance => _database;
    public SessionState State { get; private set; } = SessionState.Open;

    /// <summary>
    /// Gets the session's transaction until the caller ends it, including an aborted transaction
    /// (<see cref="TransactionState.Faulted"/>) that still waits for the caller's rollback.
    /// </summary>
    public IDatabaseTransaction? CurrentTransaction => OpenTransaction;
    private GraphDatabaseTransaction? OpenTransaction => _transaction is { IsOpen: true } transaction ? transaction : null;
    public ValueTask<IDatabaseTransaction> BeginTransactionAsync(CancellationToken cancellationToken = default)
        => BeginTransactionAsync(IsolationLevel.Snapshot, cancellationToken);
    public async ValueTask<IDatabaseTransaction> BeginTransactionAsync(IsolationLevel isolationLevel, CancellationToken cancellationToken = default)
    {
        if (isolationLevel is not (IsolationLevel.Snapshot or IsolationLevel.ReadCommitted))
        {
            throw new DatabaseException("The graph engine supports Snapshot and ReadCommitted isolation.");
        }

        lock (_sync)
        {
            ThrowIfNotOpen();
            _database.ThrowIfOffline();
            if (OpenTransaction is { } open)
            {
                // BEGIN is refused while an aborted transaction waits for its rollback, as in
                // PostgreSQL's failed transaction block.
                throw open.IsUsable
                    ? new DatabaseException("A transaction or operation is already active on this session.")
                    : open.CreateRefusal();
            }
            if (_reserved || _operations.Count != 0)
            {
                throw new DatabaseException("A transaction or operation is already active on this session.");
            }
            _reserved = true;
        }
        ITransactionContext? context = null;
        try
        {
            context = await _database.Coordinator.BeginAsync(isolationLevel, cancellationToken).ConfigureAwait(false);
            lock (_sync)
            {
                ThrowIfNotOpen();
                _transaction = new GraphDatabaseTransaction(_database.Coordinator, context, _database);
                return _transaction;
            }
        }
        catch (Exception error)
        {
            if (context?.State == TransactionState.Active && !_database.IsOffline)
            {
                await _database.Coordinator.RollbackAsync(context).ConfigureAwait(false);
            }

            // A begin that met the offline storage (#1243) gets the coded refusal.
            var reported = _database.TranslateOffline(error);
            if (ReferenceEquals(reported, error)) { throw; }
            throw reported;
        }
        finally
        {
            ReleaseReservation();
        }
    }
    public ValueTask<QueryResult> ExecuteAsync(QueryRequest request, CancellationToken cancellationToken = default)
    {
        ThrowIfNotOpen();
        _database.ThrowIfOffline();
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        if (request.Statement is not GqlQueryStatement statement)
        {
            throw new DatabaseException("A Graph session accepts only GQL statements.");
        }
        return _database.RunAsync(this, operation => GraphPlanExecutor.ExecuteAsync(_database, operation, statement,
            request.Parameters, cancellationToken, paths: request is GraphPathsQueryRequest), cancellationToken);
    }
    public ValueTask<QueryResult> ExecuteAsync(string statement, IReadOnlyDictionary<string, object?>? parameters = null, CancellationToken cancellationToken = default)
    {
        ThrowIfNotOpen();
        _database.ThrowIfOffline();
        ArgumentException.ThrowIfNullOrWhiteSpace(statement);
        cancellationToken.ThrowIfCancellationRequested();
        return ExecuteStatementAsync(() => GraphQueryRequest.FromGql(statement, parameters), cancellationToken);
    }

    /// <summary>
    /// Parses and executes one statement. An aborted transaction refuses the statement before it is
    /// parsed; a parse or request-validation failure aborts the explicit transaction exactly as an
    /// execution failure does, so every failed statement has one outcome.
    /// </summary>
    /// <param name="parse">Parses and validates the statement text into its request.</param>
    /// <param name="cancellationToken">Cancellation token for the operation.</param>
    /// <returns>The statement's result.</returns>
    internal async ValueTask<QueryResult> ExecuteStatementAsync(Func<QueryRequest> parse, CancellationToken cancellationToken)
    {
        ThrowIfNotOpen();
        _database.ThrowIfOffline();
        ThrowIfTransactionAborted();
        QueryRequest request;
        try
        {
            request = parse();
        }
        catch (DatabaseException error)
        {
            await AbortTransactionAsync(error).ConfigureAwait(false);
            throw;
        }
        return await ExecuteAsync(request, cancellationToken).ConfigureAwait(false);
    }
    internal GraphDatabaseTransaction? ReserveOperation()
    {
        lock (_sync)
        {
            ThrowIfNotOpen();
            if (_reserved || _operations.Count != 0)
            {
                throw new DatabaseException("Dispose the active graph operation before starting another operation on this session.");
            }
            var transaction = OpenTransaction;
            if (transaction is { IsUsable: false })
            {
                throw transaction.CreateRefusal();
            }
            _reserved = true;
            return transaction;
        }
    }
    internal void ReleaseReservation()
    {
        lock (_sync)
        {
            _reserved = false;
        }
    }
    internal void Track(GraphOperation operation)
    {
        lock (_sync)
        {
            ThrowIfNotOpen();
            _operations.Add(operation);
            _reserved = false;
        }
    }
    internal void Untrack(GraphOperation operation)
    {
        lock (_sync)
        {
            _operations.Remove(operation);
        }
    }
    internal void ThrowIfNotOpen()
    {
        _database.ThrowIfDisposed();
        if (State != SessionState.Open)
        {
            throw new DatabaseException("The graph session is closed.");
        }
    }
    private void ThrowIfTransactionAborted()
    {
        if (OpenTransaction is { IsUsable: false } transaction)
        {
            throw transaction.CreateRefusal();
        }
    }

    /// <summary>
    /// Aborts the explicit transaction for a statement that failed outside its operation: before it
    /// reached one (parsing, request validation) or after its operation completed (the wire server
    /// encoding or writing its result). A statement running concurrently on the session (a caller
    /// contract violation) owns the session; its own outcome decides the transaction's.
    /// </summary>
    /// <param name="cause">The failure the caller observes.</param>
    internal async ValueTask AbortTransactionAsync(Exception cause)
    {
        GraphDatabaseTransaction? transaction;
        lock (_sync)
        {
            transaction = OpenTransaction;
            if (transaction is not { IsUsable: true } || _reserved || _operations.Count != 0)
            {
                return;
            }
            _reserved = true;
        }
        try
        {
            await transaction.AbortAsync(cause).ConfigureAwait(false);
        }
        finally
        {
            ReleaseReservation();
        }
    }
    public async ValueTask DisposeAsync()
    {
        List<GraphOperation> operations;
        lock (_sync)
        {
            if (State == SessionState.Closed)
            {
                return;
            }
            State = SessionState.Closed;
            operations = new List<GraphOperation>(_operations);
        }
        List<Exception>? errors = null;
        if (operations.Count != 0)
        {
            var closed = new DatabaseException("The graph session closed while the operation was running.");
            foreach (var operation in operations)
            {
                try
                {
                    await operation.AbortAsync(closed).ConfigureAwait(false);
                }
                catch (Exception error)
                {
                    (errors ??= []).Add(error);
                }
            }
        }
        try
        {
            if (OpenTransaction is { } transaction)
            {
                await transaction.DisposeAsync().ConfigureAwait(false);
            }
        }
        catch (Exception error)
        {
            (errors ??= []).Add(error);
        }
        if (errors is not null)
        {
            throw new AggregateException("One or more graph operations failed to close.", errors);
        }
    }
}
