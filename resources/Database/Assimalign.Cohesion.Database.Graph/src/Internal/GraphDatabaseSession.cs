using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.Graph.Language;
using Assimalign.Cohesion.Database.Transactions;

namespace Assimalign.Cohesion.Database.Graph.Internal;

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
    public IDatabaseTransaction? CurrentTransaction => _transaction;
    internal GraphDatabaseTransaction? ActiveTransaction => _transaction?.State == TransactionState.Active ? _transaction : null;
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
            if (ActiveTransaction is not null || _reserved || _operations.Count != 0)
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
                _transaction = new GraphDatabaseTransaction(_database.Coordinator, context);
                return _transaction;
            }
        }
        catch
        {
            if (context?.State == TransactionState.Active)
            {
                await _database.Coordinator.RollbackAsync(context).ConfigureAwait(false);
            }
            throw;
        }
        finally
        {
            ReleaseReservation();
        }
    }
    public ValueTask<QueryResult> ExecuteAsync(QueryRequest request, CancellationToken cancellationToken = default)
    {
        ThrowIfNotOpen();
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        if (request.Statement is not GqlQueryStatement statement)
        {
            throw new DatabaseException("A Graph session accepts only GQL statements.");
        }
        return _database.RunAsync(this, operation => GraphPlanExecutor.ExecuteAsync(_database, operation, statement, request.Parameters, cancellationToken), cancellationToken);
    }
    public ValueTask<QueryResult> ExecuteAsync(string statement, IReadOnlyDictionary<string, object?>? parameters = null, CancellationToken cancellationToken = default)
    {
        ThrowIfNotOpen();
        ArgumentException.ThrowIfNullOrWhiteSpace(statement);
        cancellationToken.ThrowIfCancellationRequested();
        return ExecuteAsync(GraphQueryRequest.FromGql(statement, parameters), cancellationToken);
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
            _reserved = true;
            return ActiveTransaction;
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
        foreach (var operation in operations)
        {
            try
            {
                await operation.AbortAsync().ConfigureAwait(false);
            }
            catch (Exception error)
            {
                (errors ??= []).Add(error);
            }
        }
        try
        {
            if (ActiveTransaction is not null)
            {
                await ActiveTransaction.DisposeAsync().ConfigureAwait(false);
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

