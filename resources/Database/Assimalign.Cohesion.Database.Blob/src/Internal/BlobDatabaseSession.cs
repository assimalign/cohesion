using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.Transactions;

namespace Assimalign.Cohesion.Database.Blob.Internal;

internal sealed class BlobDatabaseSession : IDatabaseSession
{
    private readonly BlobDatabaseInstance _database;
    private readonly HashSet<BlobOperation> _operations = [];
    private readonly object _sync = new();
    private bool _reserved;
    private BlobDatabaseTransaction? _transaction;
    internal BlobDatabaseSession(BlobDatabaseInstance database)
    {
        _database = database;
        Database = new BlobSessionDatabase(database, this);
    }
    public IDatabase Database { get; }
    public SessionState State { get; private set; } = SessionState.Open;
    public IDatabaseTransaction? CurrentTransaction => _transaction;
    internal BlobDatabaseTransaction? ActiveTransaction => _transaction?.State == TransactionState.Active ? _transaction : null;
    public ValueTask<IDatabaseTransaction> BeginTransactionAsync(CancellationToken cancellationToken = default)
        => BeginTransactionAsync(IsolationLevel.Snapshot, cancellationToken);
    public async ValueTask<IDatabaseTransaction> BeginTransactionAsync(IsolationLevel isolationLevel, CancellationToken cancellationToken = default)
    {
        if (isolationLevel is not (IsolationLevel.Snapshot or IsolationLevel.ReadCommitted))
        {
            throw new DatabaseException("The blob engine supports Snapshot and ReadCommitted isolation.");
        }

        lock (_sync)
        {
            ThrowIfNotOpen();
            if (ActiveTransaction is not null || _reserved || _operations.Count != 0)
            {
                throw new DatabaseException("A transaction or stream is already active on this session.");
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
                _transaction = new BlobDatabaseTransaction(_database.Coordinator, context);
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
        throw new DatabaseException("Blob sessions have no query language. Use the IBlobDatabase exposed by session.Database.");
    }
    public ValueTask<QueryResult> ExecuteAsync(string statement, IReadOnlyDictionary<string, object?>? parameters = null, CancellationToken cancellationToken = default)
    {
        ThrowIfNotOpen();
        ArgumentException.ThrowIfNullOrWhiteSpace(statement);
        cancellationToken.ThrowIfCancellationRequested();
        throw new DatabaseException("Blob sessions have no statement language or server-scoped commands.");
    }
    internal BlobDatabaseTransaction? ReserveOperation()
    {
        lock (_sync)
        {
            ThrowIfNotOpen();
            if (_reserved || _operations.Count != 0)
            {
                throw new DatabaseException("Dispose the active blob stream before starting another operation on this session.");
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
    internal void Track(BlobOperation operation)
    {
        lock (_sync)
        {
            ThrowIfNotOpen();
            _operations.Add(operation);
            _reserved = false;
        }
    }
    internal void Untrack(BlobOperation operation)
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
            throw new DatabaseException("The blob session is closed.");
        }
    }
    public async ValueTask DisposeAsync()
    {
        List<BlobOperation> operations;
        lock (_sync)
        {
            if (State == SessionState.Closed)
            {
                return;
            }
            State = SessionState.Closed;
            operations = new List<BlobOperation>(_operations);
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
            throw new AggregateException("One or more blob operations failed to close.", errors);
        }
    }
}

internal sealed class BlobSessionDatabase : IBlobDatabase
{
    private readonly BlobDatabaseInstance _database;
    private readonly BlobDatabaseSession _session;

    /// <summary>
    /// Initializes a new instance of the <see cref="BlobSessionDatabase"/> class.
    /// </summary>
    /// <param name="database">The blob database instance the session operates on.</param>
    /// <param name="session">The session that scopes every operation issued through this view.</param>
    public BlobSessionDatabase(BlobDatabaseInstance database, BlobDatabaseSession session)
    {
        _database = database;
        _session = session;
    }

    public DatabaseName Name => _database.Name;
    public IDatabaseEngine Engine => _database.Engine;
    public ValueTask<IDatabaseSession> CreateSessionAsync(CancellationToken cancellationToken = default)
    { _session.ThrowIfNotOpen(); return _database.CreateSessionAsync(cancellationToken); }
    public ValueTask<IBlobContainer> CreateContainerAsync(string name, CancellationToken cancellationToken = default)
        => _database.CreateContainerAsync(name, _session, cancellationToken);
    public ValueTask<IBlobContainer> GetContainerAsync(string name, CancellationToken cancellationToken = default)
        => _database.GetContainerAsync(name, _session, cancellationToken);
    public ValueTask DropContainerAsync(string name, CancellationToken cancellationToken = default)
        => _database.DropContainerAsync(name, _session, cancellationToken);
    public IAsyncEnumerable<IBlobContainer> GetContainersAsync(CancellationToken cancellationToken = default)
        => _database.GetContainersAsync(_session, cancellationToken);
    public void Dispose() => _session.DisposeAsync().AsTask().GetAwaiter().GetResult();
    public ValueTask DisposeAsync() => _session.DisposeAsync();
}
