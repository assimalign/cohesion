using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.Documents.Language;
using Assimalign.Cohesion.Database.Transactions;

namespace Assimalign.Cohesion.Database.Documents.Internal;

internal sealed class DocumentDatabaseSession : IDatabaseSession
{
    private readonly DocumentDatabaseInstance _database;
    private readonly HashSet<DocumentOperation> _operations = [];
    private readonly object _sync = new();
    private bool _reserved;
    private DocumentDatabaseTransaction? _transaction;
    internal DocumentDatabaseSession(DocumentDatabaseInstance database)
    {
        _database = database;
        Database = new DocumentSessionDatabase(database, this);
    }
    public IDatabase Database { get; }
    internal DocumentDatabaseInstance Instance => _database;
    public SessionState State { get; private set; } = SessionState.Open;
    public IDatabaseTransaction? CurrentTransaction => _transaction;
    internal DocumentDatabaseTransaction? ActiveTransaction => _transaction?.State == TransactionState.Active ? _transaction : null;
    public ValueTask<IDatabaseTransaction> BeginTransactionAsync(CancellationToken cancellationToken = default)
        => BeginTransactionAsync(IsolationLevel.Snapshot, cancellationToken);
    public async ValueTask<IDatabaseTransaction> BeginTransactionAsync(IsolationLevel isolationLevel, CancellationToken cancellationToken = default)
    {
        if (isolationLevel is not (IsolationLevel.Snapshot or IsolationLevel.ReadCommitted))
        {
            throw new DatabaseException("The document engine supports Snapshot and ReadCommitted isolation.");
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
                _transaction = new DocumentDatabaseTransaction(_database.Coordinator, context);
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
        if (request.Statement is not OqlQueryStatement statement)
        {
            throw new DatabaseException("A Documents session accepts only OQL statements.");
        }
        return _database.RunAsync(this, operation => DocumentPlanExecutor.ExecuteAsync(_database, operation, statement, request.Parameters, cancellationToken), cancellationToken);
    }
    public ValueTask<QueryResult> ExecuteAsync(string statement, IReadOnlyDictionary<string, object?>? parameters = null, CancellationToken cancellationToken = default)
    {
        ThrowIfNotOpen();
        ArgumentException.ThrowIfNullOrWhiteSpace(statement);
        cancellationToken.ThrowIfCancellationRequested();
        return ExecuteAsync(DocumentQueryRequest.FromOql(statement, parameters), cancellationToken);
    }
    internal DocumentDatabaseTransaction? ReserveOperation()
    {
        lock (_sync)
        {
            ThrowIfNotOpen();
            if (_reserved || _operations.Count != 0)
            {
                throw new DatabaseException("Dispose the active document operation before starting another operation on this session.");
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
    internal void Track(DocumentOperation operation)
    {
        lock (_sync)
        {
            ThrowIfNotOpen();
            _operations.Add(operation);
            _reserved = false;
        }
    }
    internal void Untrack(DocumentOperation operation)
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
            throw new DatabaseException("The document session is closed.");
        }
    }
    public async ValueTask DisposeAsync()
    {
        List<DocumentOperation> operations;
        lock (_sync)
        {
            if (State == SessionState.Closed)
            {
                return;
            }
            State = SessionState.Closed;
            operations = new List<DocumentOperation>(_operations);
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
            throw new AggregateException("One or more document operations failed to close.", errors);
        }
    }
}

internal sealed class DocumentSessionDatabase : IDocumentDatabase
{
    private readonly DocumentDatabaseInstance _database;
    private readonly DocumentDatabaseSession _session;

    /// <summary>
    /// Initializes a new instance of the <see cref="DocumentSessionDatabase"/> class.
    /// </summary>
    /// <param name="database">The document database instance the session belongs to.</param>
    /// <param name="session">The session this database view is bound to.</param>
    public DocumentSessionDatabase(DocumentDatabaseInstance database, DocumentDatabaseSession session)
    {
        _database = database;
        _session = session;
    }

    public DatabaseName Name => _database.Name;
    public IDatabaseEngine Engine => _database.Engine;
    public ValueTask<IDatabaseSession> CreateSessionAsync(CancellationToken cancellationToken = default)
    { _session.ThrowIfNotOpen(); return _database.CreateSessionAsync(cancellationToken); }
    public ValueTask<IDocumentCollection> CreateCollectionAsync(string name, CancellationToken cancellationToken = default)
        => _database.CreateCollectionAsync(name, _session, cancellationToken);
    public ValueTask<IDocumentCollection> GetCollectionAsync(string name, CancellationToken cancellationToken = default)
        => _database.GetCollectionAsync(name, _session, cancellationToken);
    public ValueTask DropCollectionAsync(string name, CancellationToken cancellationToken = default)
        => _database.DropCollectionAsync(name, _session, cancellationToken);
    public IAsyncEnumerable<IDocumentCollection> GetCollectionsAsync(CancellationToken cancellationToken = default)
        => _database.GetCollectionsAsync(_session, cancellationToken);
    public void Dispose() => _session.DisposeAsync().AsTask().GetAwaiter().GetResult();
    public ValueTask DisposeAsync() => _session.DisposeAsync();
}
