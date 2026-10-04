using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.Transactions;

namespace Assimalign.Cohesion.Database.Blob.Internal;

/// <summary>
/// A blob session. Its explicit transaction stays the session's transaction until the caller
/// commits, rolls back or disposes it; an operation that fails inside it aborts it, and the session
/// then refuses every operation and BEGIN with <c>COHDBB001</c> until the caller rolls back. A
/// failure never turns later operations into autocommit writes (#1225).
/// </summary>
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

    /// <summary>
    /// Gets the session's transaction until the caller ends it, including an aborted transaction
    /// (<see cref="TransactionState.Faulted"/>) that still waits for the caller's rollback.
    /// </summary>
    public IDatabaseTransaction? CurrentTransaction => OpenTransaction;
    private BlobDatabaseTransaction? OpenTransaction => _transaction is { IsOpen: true } transaction ? transaction : null;
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
            _database.ThrowIfOffline();
            if (OpenTransaction is { } open)
            {
                // BEGIN is refused while an aborted transaction waits for its rollback, as in
                // PostgreSQL's failed transaction block.
                throw open.IsUsable
                    ? new DatabaseException("A transaction or stream is already active on this session.")
                    : open.CreateRefusal();
            }
            if (_reserved || _operations.Count != 0)
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
                _transaction = new BlobDatabaseTransaction(_database.Coordinator, context, _database);
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
        throw new DatabaseException("Blob sessions have no query language. Use the IBlobDatabase exposed by session.Database.");
    }
    public ValueTask<QueryResult> ExecuteAsync(string statement, IReadOnlyDictionary<string, object?>? parameters = null, CancellationToken cancellationToken = default)
    {
        ThrowIfNotOpen();
        _database.ThrowIfOffline();
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

    /// <summary>
    /// Aborts the session's explicit transaction for a failure that ends the session, unless an
    /// operation already aborted it. The wire server calls it before it reports a terminal failure,
    /// so a failure that came before an operation started (which leaves an in-process transaction
    /// unchanged) has aborted a host-opened transaction before the client sees the error, and the
    /// host's commit fails with <c>COHDBB001</c> naming it whichever of the commit and the
    /// connection's teardown runs first.
    /// </summary>
    /// <param name="cause">The failure the client is told about.</param>
    internal async ValueTask AbortTransactionAsync(Exception cause)
    {
        if (OpenTransaction is { IsUsable: true } transaction)
        {
            await transaction.AbortAsync(cause).ConfigureAwait(false);
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
        if (operations.Count != 0)
        {
            var closed = new DatabaseException("The blob session closed while the operation was running.");
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
            // The transaction object stays with its caller: a later rollback is a no-op, and a later
            // commit fails with COHDBB001 naming the closure (or the operation failure before it).
            // Over the wire this is the server session's teardown under a host-opened transaction.
            if (OpenTransaction is { } transaction)
            {
                await transaction.CloseAsync(new DatabaseException("The blob session closed before the transaction ended.")).ConfigureAwait(false);
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
