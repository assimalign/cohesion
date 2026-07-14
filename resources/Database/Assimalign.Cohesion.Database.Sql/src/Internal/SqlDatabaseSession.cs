using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Database.Sql.Internal;

using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.Sql.Storage;
using Assimalign.Cohesion.Database.Storage;
using Assimalign.Cohesion.Database.Transactions;

/// <summary>
/// Internal implementation of a SQL database session.
/// </summary>
internal sealed class SqlDatabaseSession : IDatabaseSession
{
    private readonly SqlStorage _storage;
    private readonly SqlQueryExecutor _executor;

    private SqlDatabaseTransaction? _transaction;
    private SessionState _state;

    internal SqlDatabaseSession(ISqlDatabase database, SqlStorage storage, SqlQueryExecutor executor)
    {
        Database = database;
        _storage = storage;
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
    /// The requested level is carried on the transaction; execution is
    /// conservative — writers serialize at page grain through the storage
    /// transaction (stronger than any level's write behavior). Per-level snapshot
    /// visibility arrives with the MVCC session binding.
    /// </remarks>
    public ValueTask<IDatabaseTransaction> BeginTransactionAsync(IsolationLevel isolationLevel, CancellationToken cancellationToken = default)
    {
        ThrowIfNotOpen();
        cancellationToken.ThrowIfCancellationRequested();

        if (_transaction is not null && _transaction.State == TransactionState.Active)
        {
            throw new DatabaseException("A transaction is already active on this session.");
        }

        _transaction = new SqlDatabaseTransaction(TransactionId.NewId(), _storage.BeginTransaction(), isolationLevel);

        return new ValueTask<IDatabaseTransaction>(_transaction);
    }

    /// <inheritdoc />
    public async ValueTask<QueryResult> ExecuteAsync(QueryRequest request, CancellationToken cancellationToken = default)
    {
        ThrowIfNotOpen();
        ArgumentNullException.ThrowIfNull(request);

        // If there is an active transaction, delegate to the executor with its storage scope
        if (_transaction is not null && _transaction.State == TransactionState.Active)
        {
            return await _executor.ExecuteAsync(request, _transaction.StorageTransaction, cancellationToken).ConfigureAwait(false);
        }

        // Auto-commit semantics: wrap in a mini-transaction
        var autoTx = new SqlDatabaseTransaction(TransactionId.NewId(), _storage.BeginTransaction());

        try
        {
            var result = await _executor.ExecuteAsync(request, autoTx.StorageTransaction, cancellationToken).ConfigureAwait(false);
            await autoTx.CommitAsync(cancellationToken).ConfigureAwait(false);
            return result;
        }
        catch
        {
            if (autoTx.State == TransactionState.Active)
            {
                await autoTx.RollbackAsync().ConfigureAwait(false);
            }

            throw;
        }
    }

    /// <inheritdoc />
    /// <remarks>
    /// The model-agnostic text-execute seam: SQL sessions parse the statement with
    /// the SQL dialect (<see cref="SqlQueryRequest.FromSql"/>) — this is what lets
    /// the wire-protocol server execute statement text without knowing any model
    /// language.
    /// </remarks>
    public ValueTask<QueryResult> ExecuteAsync(string statement, IReadOnlyDictionary<string, object?>? parameters = null, CancellationToken cancellationToken = default)
    {
        ThrowIfNotOpen();
        ArgumentException.ThrowIfNullOrWhiteSpace(statement);

        return ExecuteAsync(SqlQueryRequest.FromSql(statement, parameters), cancellationToken);
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
