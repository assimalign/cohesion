using System;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Database.Sql.Internal;

using Assimalign.Cohesion.Database.Sql.Catalog;
using Assimalign.Cohesion.Database.Sql.Storage;

/// <summary>
/// Internal implementation of a SQL database instance: the data storage, the
/// dedicated catalog storage, the catalog opened over it, and the transaction
/// coordinator — the per-database MVCC composition (transaction manager, lock
/// manager, version store) every session binds to.
/// </summary>
internal sealed class SqlDatabaseInstance : ISqlDatabase
{
    private readonly SqlStorage _storage;
    private readonly SqlStorage _catalogStorage;
    private readonly ISqlCatalog _catalog;
    private readonly SqlTransactionCoordinator _coordinator;
    private bool _disposed;

    internal SqlDatabaseInstance(string name, IDatabaseEngine engine, SqlStorage storage, SqlStorage catalogStorage, bool recover = false)
    {
        Name = name;
        Engine = engine;
        _storage = storage;
        _catalogStorage = catalogStorage;
        _catalog = SqlCatalog.Open(catalogStorage);
        _coordinator = new SqlTransactionCoordinator(storage);

        if (recover)
        {
            // Reopened storage: classify the recovered journal, purge unproven
            // writers from the version store, then checkpoint (the open-time
            // checkpoint the storage strategy deferred).
            _coordinator.Recover();
        }
    }

    /// <inheritdoc />
    public DatabaseName Name { get; }

    /// <inheritdoc />
    public IDatabaseEngine Engine { get; }

    /// <summary>
    /// Gets the data storage file set, for the engine's background workers.
    /// </summary>
    internal SqlStorage DataStorage => _storage;

    /// <summary>
    /// Gets the dedicated catalog storage file set, for the engine's background workers.
    /// </summary>
    internal SqlStorage CatalogStorage => _catalogStorage;

    /// <summary>
    /// Gets the database's transaction coordinator (the MVCC composition sessions
    /// bind to), for the engine's background workers and tests.
    /// </summary>
    internal SqlTransactionCoordinator Coordinator => _coordinator;

    /// <summary>
    /// Checkpoints the data storage through the coordinator, so the truncating
    /// checkpoint record carries the sequences of in-flight logical transactions
    /// (recovery classification stays sound). The catalog storage has no logical
    /// transactions above it and checkpoints directly.
    /// </summary>
    internal void CheckpointDataStorage() => _coordinator.Checkpoint();

    /// <inheritdoc />
    public ValueTask<IDatabaseSession> CreateSessionAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        cancellationToken.ThrowIfCancellationRequested();

        var executor = new SqlQueryExecutor(_storage, _catalog);
        var session = new SqlDatabaseSession(this, _coordinator, executor);

        return new ValueTask<IDatabaseSession>(session);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        // The coordinator first: the manager aborts every still-active logical
        // transaction (rolling its paired bracket back) while the storage is
        // still open. Synchronous over the ValueTask by design — the in-process
        // implementations complete synchronously.
        _coordinator.DisposeAsync().AsTask().GetAwaiter().GetResult();
        _storage.Dispose();
        _catalogStorage.Dispose();
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        await _coordinator.DisposeAsync().ConfigureAwait(false);
        await _storage.DisposeAsync().ConfigureAwait(false);
        await _catalogStorage.DisposeAsync().ConfigureAwait(false);
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }
}
