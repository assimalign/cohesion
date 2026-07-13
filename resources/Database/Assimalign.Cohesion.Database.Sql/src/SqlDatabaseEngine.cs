using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Database.Sql;

using Assimalign.Cohesion.Database.Sql.Storage;

using Internal;

/// <summary>
/// SQL database engine that manages the lifecycle of SQL database instances.
/// </summary>
/// <remarks>
/// The engine is a data machine: <see cref="Create"/> returns it fully operational —
/// the storage strategy is resolved and the engine-owned background workers
/// (write-ahead-log group-commit flusher, page write-back, checkpointer, and the
/// maintenance stubs) are already pumping on dedicated threads the engine spawned —
/// and disposal is its one lifecycle transition: quiesce the workers, durably flush
/// and close every open database. Each database is backed by a storage strategy
/// managing two file sets (data and <c>.catalog</c>); the strategy is file-based
/// when <see cref="SqlDatabaseEngineOptions.RootPath"/> is set and in-memory
/// otherwise. The journal is owned per-database through the storage layer, so there
/// is no engine-level write-ahead log.
/// </remarks>
public sealed class SqlDatabaseEngine : IDatabaseEngine
{
    private readonly SqlDatabaseEngineOptions _options;
    private readonly Dictionary<string, IDatabase> _databases = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _syncRoot = new();
    private readonly DatabaseEngineWorker[] _workers;
    private readonly ManualResetEventSlim _commitPendingSignal = new();
    private readonly Action _signalCommitPending;
    private readonly List<Thread> _workerThreads = new();
    private readonly CancellationTokenSource _workerStopSource = new();
    private readonly ISqlStorageStrategy _strategy;

    private SqlStorage[] _storageSnapshot = [];
    private Exception? _workerFault;
    private bool _disposed;

    /// <summary>
    /// The storage-name suffix of the dedicated catalog file set each database owns.
    /// </summary>
    internal const string CatalogSuffix = ".catalog";

    private SqlDatabaseEngine(SqlDatabaseEngineOptions options)
    {
        _options = options;
        Name = options.EngineName ?? "sql-engine";
        _signalCommitPending = _commitPendingSignal.Set;

        // Resolve the storage strategy at creation: the engine is operational from
        // the moment the constructor returns (create → use → dispose; no start).
        _strategy = options.StorageStrategy
            ?? (string.IsNullOrWhiteSpace(options.RootPath)
                ? new InMemorySqlStorageStrategy()
                : new FileSystemSqlStorageStrategy(options.RootPath));

        if (!string.IsNullOrWhiteSpace(options.RootPath))
        {
            Directory.CreateDirectory(options.RootPath);
        }

        _workers =
        [
            new SqlWriteAheadFlushWorker(this, _commitPendingSignal),
            new SqlPageWriteBackWorker(this),
            new SqlCheckpointWorker(this),
            new SqlVersionPurgeWorker(this),
            new SqlIndexMaintenanceWorker(this),
        ];

        // Spawn the worker pumps last, after every field they observe is
        // initialized: one dedicated background thread per worker, alive until
        // disposal. Embedded and hosted consumers get identical durability
        // behavior because nothing outside the engine participates (R10).
        StartWorkerThreads();
    }

    /// <inheritdoc />
    public string Name { get; }

    /// <inheritdoc />
    public EngineState State
    {
        get
        {
            if (_disposed)
            {
                return EngineState.Disposed;
            }

            return Volatile.Read(ref _workerFault) is null ? EngineState.Running : EngineState.Faulted;
        }
    }

    /// <inheritdoc />
    public EngineModel Model => EngineModel.Sql;

    /// <inheritdoc />
    public IReadOnlyList<IDatabaseEngineWorker> Workers => _workers;

    /// <summary>
    /// Gets the engine options, for the engine's background workers.
    /// </summary>
    internal SqlDatabaseEngineOptions EngineOptions => _options;

    /// <summary>
    /// Gets a point-in-time snapshot of every open storage file set (the data and
    /// catalog sets of every open database), for the engine's background workers.
    /// The snapshot is rebuilt when databases open or close; a worker pass may
    /// therefore race a drop, which workers tolerate.
    /// </summary>
    internal SqlStorage[] GetStorageSnapshot() => Volatile.Read(ref _storageSnapshot);

    /// <summary>
    /// Creates a new SQL database engine from options. The engine is operational —
    /// background workers running — when this method returns.
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

            var storage = ConfigureStorage(_strategy.CreateStorage(name));
            var catalogStorage = ConfigureStorage(_strategy.CreateStorage(name + CatalogSuffix));
            var database = new SqlDatabaseInstance(name, this, storage, catalogStorage);
            _databases[name] = database;
            RebuildStorageSnapshotLocked();

            return new ValueTask<IDatabase>(database);
        }
    }

    /// <inheritdoc />
    public ValueTask<IDatabase> OpenDatabaseAsync(string name, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();

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

            if (!_strategy.StorageExists(name))
            {
                throw new DatabaseException($"Database '{name}' does not exist.");
            }

            var storage = ConfigureStorage(_strategy.OpenStorage(name));
            var catalogStorage = ConfigureStorage(_strategy.StorageExists(name + CatalogSuffix)
                ? _strategy.OpenStorage(name + CatalogSuffix)
                : _strategy.CreateStorage(name + CatalogSuffix));
            var database = new SqlDatabaseInstance(name, this, storage, catalogStorage);
            _databases[name] = database;
            RebuildStorageSnapshotLocked();

            return new ValueTask<IDatabase>(database);
        }
    }

    /// <inheritdoc />
    public ValueTask DropDatabaseAsync(string name, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();

        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("Database name cannot be null or empty.", nameof(name));
        }

        lock (_syncRoot)
        {
            if (_databases.TryGetValue(name, out var database))
            {
                // Publish the shrunken snapshot before disposing so worker passes
                // stop touching the storage as early as possible (a pass already in
                // flight may still race the dispose, which workers tolerate).
                _databases.Remove(name);
                RebuildStorageSnapshotLocked();
                database.Dispose();
            }

            _strategy.DropStorage(name);

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
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        // Quiesce the worker pumps before closing storages: no worker pass may
        // touch a database that is being disposed.
        StopWorkerThreads();

        lock (_syncRoot)
        {
            // Closing a database durably flushes it: storage disposal checkpoints
            // when no transaction is active (clean shutdown) and force-flushes the
            // journal otherwise, so committed work is on stable storage when
            // disposal completes.
            foreach (var database in _databases.Values)
            {
                database.Dispose();
            }
            _databases.Clear();
            _storageSnapshot = [];
        }

        _workerStopSource.Dispose();
        _commitPendingSignal.Dispose();
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        StopWorkerThreads();

        IDatabase[] snapshot;
        lock (_syncRoot)
        {
            snapshot = [.. _databases.Values];
            _databases.Clear();
            _storageSnapshot = [];
        }

        foreach (var database in snapshot)
        {
            await database.DisposeAsync().ConfigureAwait(false);
        }

        _workerStopSource.Dispose();
        _commitPendingSignal.Dispose();
    }

    /// <summary>
    /// Configures a freshly created or opened storage file set with the engine's
    /// durability policy and wires its commit-pending hook to the engine's flush
    /// worker signal.
    /// </summary>
    private SqlStorage ConfigureStorage(SqlStorage storage)
    {
        storage.CommitDurability = _options.Durability;
        storage.GroupCommitWindow = _options.GroupCommitWindow;
        storage.OnCommitPending = _signalCommitPending;
        return storage;
    }

    /// <summary>
    /// Rebuilds the storage snapshot the background workers iterate. Called under
    /// the engine lock whenever the open-database set changes.
    /// </summary>
    private void RebuildStorageSnapshotLocked()
    {
        var storages = new SqlStorage[_databases.Count * 2];
        int index = 0;

        foreach (var database in _databases.Values)
        {
            var instance = (SqlDatabaseInstance)database;
            storages[index++] = instance.DataStorage;
            storages[index++] = instance.CatalogStorage;
        }

        Volatile.Write(ref _storageSnapshot, storages);
    }

    /// <summary>
    /// Spawns one dedicated background thread per worker at engine creation. The
    /// engine owns every loop; there is no external scheduler.
    /// </summary>
    private void StartWorkerThreads()
    {
        foreach (var worker in _workers)
        {
            var thread = new Thread(() => PumpWorker(worker, _workerStopSource.Token))
            {
                IsBackground = true,
                Name = worker.Name,
            };

            _workerThreads.Add(thread);
            thread.Start();
        }
    }

    /// <summary>
    /// The worker pump frame: runs the worker until the engine is disposed. A
    /// cooperative cancellation exit is a clean stop; any other fault is recorded —
    /// the engine reports <see cref="EngineState.Faulted"/> and keeps serving (a
    /// faulted worker never compromises correctness: grouped commits self-help
    /// within their window, checkpoints simply stop truncating), but the owner can
    /// observe that the engine runs degraded. An escaped exception on a raw thread
    /// would terminate the process.
    /// </summary>
    private void PumpWorker(DatabaseEngineWorker worker, CancellationToken cancellationToken)
    {
        try
        {
            worker.Run(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            // Clean stop.
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            Interlocked.CompareExchange(ref _workerFault, exception, null);
        }
    }

    /// <summary>
    /// Signals and joins the worker pumps (disposal path).
    /// </summary>
    private void StopWorkerThreads()
    {
        _workerStopSource.Cancel();

        foreach (var thread in _workerThreads)
        {
            thread.Join();
        }

        _workerThreads.Clear();
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }
}
