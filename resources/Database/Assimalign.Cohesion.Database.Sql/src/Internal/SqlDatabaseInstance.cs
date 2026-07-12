using System;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Database.Sql.Internal;

using Assimalign.Cohesion.Database.Sql.Catalog;
using Assimalign.Cohesion.Database.Sql.Storage;

/// <summary>
/// Internal implementation of a SQL database instance: the data storage, the
/// dedicated catalog storage, and the catalog opened over it.
/// </summary>
internal sealed class SqlDatabaseInstance : ISqlDatabase
{
    private readonly SqlStorage _storage;
    private readonly SqlStorage _catalogStorage;
    private readonly ISqlCatalog _catalog;
    private bool _disposed;

    internal SqlDatabaseInstance(string name, IDatabaseEngine engine, SqlStorage storage, SqlStorage catalogStorage)
    {
        Name = name;
        Engine = engine;
        _storage = storage;
        _catalogStorage = catalogStorage;
        _catalog = SqlCatalog.Open(catalogStorage);
    }

    /// <inheritdoc />
    public DatabaseName Name { get; }

    /// <inheritdoc />
    public IDatabaseEngine Engine { get; }

    /// <inheritdoc />
    public ValueTask<IDatabaseSession> CreateSessionAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        cancellationToken.ThrowIfCancellationRequested();

        var executor = new SqlQueryExecutor(_storage, _catalog);
        var session = new SqlDatabaseSession(this, _storage, executor);

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
        await _storage.DisposeAsync().ConfigureAwait(false);
        await _catalogStorage.DisposeAsync().ConfigureAwait(false);
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }
}
