using System;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Database.Sql.Internal;

using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.Sql.Storage;
using Assimalign.Cohesion.Database.Storage;

/// <summary>
/// Internal implementation of a SQL database session.
/// </summary>
internal sealed class SqlDatabaseSession : IDatabaseSession
{
    private readonly SqlStorage _storage;
    private readonly SqlQueryExecutor _executor;
    private readonly IJournalLogger _journal;

    private SqlDatabaseTransaction? _transaction;
    private SessionState _state;

    internal SqlDatabaseSession(ISqlDatabase database, SqlStorage storage, SqlQueryExecutor executor)
    {
        Database = database;
        _storage = storage;
        _executor = executor;
        _journal = storage.GetJournalLogger();
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
    {
        ThrowIfNotOpen();
        cancellationToken.ThrowIfCancellationRequested();

        if (_transaction is not null && _transaction.State == TransactionState.Active)
        {
            throw new DatabaseException("A transaction is already active on this session.");
        }

        var journalTxId = _journal.BeginTransaction("Sql", "default");
        var transactionId = TransactionId.NewId();

        _transaction = new SqlDatabaseTransaction(transactionId, journalTxId, _journal, _storage);

        return new ValueTask<IDatabaseTransaction>(_transaction);
    }

    /// <inheritdoc />
    public async ValueTask<QueryResult> ExecuteAsync(QueryRequest request, CancellationToken cancellationToken = default)
    {
        ThrowIfNotOpen();
        ArgumentNullException.ThrowIfNull(request);

        // If there is an active transaction, delegate to the executor with its journal tx ID
        if (_transaction is not null && _transaction.State == TransactionState.Active)
        {
            return await _executor.ExecuteAsync(request, _transaction.JournalTransactionId, cancellationToken).ConfigureAwait(false);
        }

        // Auto-commit semantics: wrap in a mini-transaction
        var journalTxId = _journal.BeginTransaction("Sql", "default");
        var autoTx = new SqlDatabaseTransaction(TransactionId.NewId(), journalTxId, _journal, _storage);

        try
        {
            var result = await _executor.ExecuteAsync(request, journalTxId, cancellationToken).ConfigureAwait(false);
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
