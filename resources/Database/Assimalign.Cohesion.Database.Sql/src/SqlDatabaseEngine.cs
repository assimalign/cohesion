using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Database.Sql;

using Internal;

/// <summary>
/// SQL database engine that manages the lifecycle of SQL database instances.
/// </summary>
/// <remarks>
/// Each database is backed by a storage strategy that manages three file assets:
/// data (<c>.dat</c>), journal (<c>.log</c>), and backup (<c>.bak</c>).
/// The storage strategy can be file-based (default when <see cref="SqlDatabaseEngineOptions.RootPath"/>
/// is set) or in-memory (default when no root path is provided).
/// The journal is owned per-database through the storage layer, so there is no
/// engine-level write-ahead log.
/// </remarks>
public sealed class SqlDatabaseEngine : IDatabaseEngine
{
    private readonly SqlDatabaseEngineOptions _options;
    private readonly Dictionary<string, IDatabase> _databases = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _syncRoot = new();

    private ISqlStorageStrategy? _strategy;
    private EngineState _state;
    private bool _disposed;

    /// <summary>
    /// The storage-name suffix of the dedicated catalog file set each database owns.
    /// </summary>
    internal const string CatalogSuffix = ".catalog";

    private SqlDatabaseEngine(SqlDatabaseEngineOptions options)
    {
        _options = options;
        Name = options.EngineName ?? "sql-engine";
        _state = EngineState.Idle;
    }

    /// <inheritdoc />
    public string Name { get; }

    /// <inheritdoc />
    public EngineState State => _state;

    /// <inheritdoc />
    public EngineModel Model => EngineModel.Sql;

    /// <summary>
    /// Creates a new SQL database engine from options.
    /// </summary>
    /// <param name="options">Engine creation options.</param>
    /// <returns>A new engine instance.</returns>
    public static SqlDatabaseEngine Create(SqlDatabaseEngineOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return new SqlDatabaseEngine(options);
    }

    /// <inheritdoc />
    public ValueTask<IDatabase> CreateDatabaseAsync(string name, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ThrowIfNotRunning();

        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("Database name cannot be null or empty.", nameof(name));
        }

        lock (_syncRoot)
        {
            if (_databases.ContainsKey(name))
            {
                throw new DatabaseException($"A database with name '{name}' already exists.");
            }

            var storage = _strategy!.CreateStorage(name);
            var catalogStorage = _strategy.CreateStorage(name + CatalogSuffix);
            var database = new SqlDatabaseInstance(name, this, storage, catalogStorage);
            _databases[name] = database;

            return new ValueTask<IDatabase>(database);
        }
    }

    /// <inheritdoc />
    public ValueTask<IDatabase> OpenDatabaseAsync(string name, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ThrowIfNotRunning();

        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("Database name cannot be null or empty.", nameof(name));
        }

        lock (_syncRoot)
        {
            if (_databases.TryGetValue(name, out var existing))
            {
                return new ValueTask<IDatabase>(existing);
            }

            if (!_strategy!.StorageExists(name))
            {
                throw new DatabaseException($"Database '{name}' does not exist.");
            }

            var storage = _strategy.OpenStorage(name);
            var catalogStorage = _strategy.StorageExists(name + CatalogSuffix)
                ? _strategy.OpenStorage(name + CatalogSuffix)
                : _strategy.CreateStorage(name + CatalogSuffix);
            var database = new SqlDatabaseInstance(name, this, storage, catalogStorage);
            _databases[name] = database;

            return new ValueTask<IDatabase>(database);
        }
    }

    /// <inheritdoc />
    public ValueTask DropDatabaseAsync(string name, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ThrowIfNotRunning();

        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("Database name cannot be null or empty.", nameof(name));
        }

        lock (_syncRoot)
        {
            if (_databases.TryGetValue(name, out var database))
            {
                database.Dispose();
                _databases.Remove(name);
            }

            _strategy!.DropStorage(name);

            if (_strategy.StorageExists(name + CatalogSuffix))
            {
                _strategy.DropStorage(name + CatalogSuffix);
            }
        }

        return default;
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<IDatabase> GetDatabasesAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ThrowIfNotRunning();

        IDatabase[] snapshot;
        lock (_syncRoot)
        {
            snapshot = [.. _databases.Values];
        }

        foreach (var database in snapshot)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return database;
        }

        await Task.CompletedTask.ConfigureAwait(false);
    }

    /// <inheritdoc />
    public bool TryGetDatabase(string name, out IDatabase database)
    {
        ThrowIfDisposed();

        lock (_syncRoot)
        {
            return _databases.TryGetValue(name, out database!);
        }
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        cancellationToken.ThrowIfCancellationRequested();

        if (_state == EngineState.Running)
        {
            return Task.CompletedTask;
        }

        _state = EngineState.Starting;

        // Resolve the storage strategy
        _strategy = _options.StorageStrategy
            ?? (string.IsNullOrWhiteSpace(_options.RootPath)
                ? new InMemorySqlStorageStrategy()
                : new FileSystemSqlStorageStrategy(_options.RootPath));

        // Ensure root directory exists for file-based strategies
        if (!string.IsNullOrWhiteSpace(_options.RootPath))
        {
            Directory.CreateDirectory(_options.RootPath);
        }

        _state = EngineState.Running;
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        cancellationToken.ThrowIfCancellationRequested();

        if (_state != EngineState.Running)
        {
            return Task.CompletedTask;
        }

        _state = EngineState.Stopping;

        lock (_syncRoot)
        {
            foreach (var database in _databases.Values)
            {
                database.Dispose();
            }
            _databases.Clear();
        }

        _state = EngineState.Stopped;
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        lock (_syncRoot)
        {
            foreach (var database in _databases.Values)
            {
                database.Dispose();
            }
            _databases.Clear();
        }

        _state = EngineState.Stopped;
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        IDatabase[] snapshot;
        lock (_syncRoot)
        {
            snapshot = [.. _databases.Values];
            _databases.Clear();
        }

        foreach (var database in snapshot)
        {
            await database.DisposeAsync().ConfigureAwait(false);
        }

        _state = EngineState.Stopped;
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    private void ThrowIfNotRunning()
    {
        if (_state != EngineState.Running)
        {
            throw new DatabaseException($"Engine is not running. Current state: {_state}.");
        }
    }
}
