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
    private readonly IDatabaseEngineWorker[] _workers;
    private readonly ManualResetEventSlim _commitPendingSignal = new();
    private readonly Action _signalCommitPending;
    private readonly List<(IDatabaseEngineWorker Worker, Thread Thread)> _selfScheduled = new();

    private ISqlStorageStrategy? _strategy;
    private SqlStorage[] _storageSnapshot = [];
    private CancellationTokenSource? _workerStopSource;
    private Exception? _workerFault;
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
        _signalCommitPending = _commitPendingSignal.Set;
        _workers =
        [
            new SqlWriteAheadFlushWorker(this, _commitPendingSignal),
            new SqlPageWriteBackWorker(this),
            new SqlCheckpointWorker(this),
            new SqlVersionPurgeWorker(this),
            new SqlIndexMaintenanceWorker(this),
        ];
    }

    /// <inheritdoc />
    public string Name { get; }

    /// <inheritdoc />
    public EngineState State => _state;

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

            var storage = ConfigureStorage(_strategy!.CreateStorage(name));
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
        ThrowIfNotRunning();

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

        lock (_syncRoot)
        {
            switch (_state)
            {
                case EngineState.Running:
                    return Task.CompletedTask;
                case EngineState.Starting or EngineState.Stopping:
                    throw new DatabaseException($"Engine '{Name}' has a lifecycle transition in progress ({_state}).");
                case EngineState.Faulted:
                    throw new DatabaseException($"Engine '{Name}' is faulted and cannot be started.");
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

            // Self-schedule every background worker nobody claimed: an embedded
            // consumer (no host) gets identical checkpoint/flush/write-back behavior
            // out of the box, while a host that claimed workers before this start
            // drives them on its own execution menu instead (R10 — the engine owns
            // the work; the scheduler is pluggable).
            StartSelfScheduledWorkersLocked();

            _state = EngineState.Running;
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        cancellationToken.ThrowIfCancellationRequested();

        IDatabase[] snapshot;

        lock (_syncRoot)
        {
            if (_state is EngineState.Starting or EngineState.Stopping)
            {
                throw new DatabaseException($"Engine '{Name}' has a lifecycle transition in progress ({_state}).");
            }

            if (_state != EngineState.Running)
            {
                return;
            }

            _state = EngineState.Stopping;
            snapshot = [.. _databases.Values];
            _databases.Clear();
            _storageSnapshot = [];
        }

        // Quiesce the self-scheduled workers before closing storages: no worker pass
        // may touch a database that is being disposed. Host-claimed workers were
        // already stopped by the host (its worker slots stop before the engines).
        StopSelfScheduledWorkers();

        // Closing a database durably flushes it: storage disposal checkpoints when no
        // transaction is active (clean shutdown) and force-flushes the journal otherwise,
        // so committed work is on stable storage before the stop completes.
        foreach (var database in snapshot)
        {
            await database.DisposeAsync().ConfigureAwait(false);
        }

        lock (_syncRoot)
        {
            _state = EngineState.Stopped;
        }

        // Surface a background-worker fault collected during the run: the work kept
        // its correctness guarantees (commits self-help, checkpoints retry), but the
        // owner must learn the engine ran degraded.
        Exception? fault = Interlocked.Exchange(ref _workerFault, null);
        if (fault is not null)
        {
            throw new DatabaseException($"Engine '{Name}' background worker faulted during the run. See inner exception.", fault);
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

        StopSelfScheduledWorkers();

        lock (_syncRoot)
        {
            foreach (var database in _databases.Values)
            {
                database.Dispose();
            }
            _databases.Clear();
            _storageSnapshot = [];
        }

        _commitPendingSignal.Dispose();
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

        StopSelfScheduledWorkers();

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

        _commitPendingSignal.Dispose();
        _state = EngineState.Stopped;
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
    /// Claims and self-schedules every worker no host claimed, one dedicated
    /// background thread per worker (the embedded execution model — R10).
    /// </summary>
    private void StartSelfScheduledWorkersLocked()
    {
        var stopSource = new CancellationTokenSource();
        _workerStopSource = stopSource;

        foreach (var worker in _workers)
        {
            if (!worker.TryClaim())
            {
                // A host claimed this worker before start and drives it on its own
                // execution menu.
                continue;
            }

            var thread = new Thread(() => PumpWorker(worker, stopSource.Token))
            {
                IsBackground = true,
                Name = worker.Name,
            };

            _selfScheduled.Add((worker, thread));
            thread.Start();
        }
    }

    /// <summary>
    /// The self-scheduled worker pump frame: runs the worker until the engine stops.
    /// A cooperative cancellation exit is a clean stop; any other fault is recorded
    /// and surfaced by <see cref="StopAsync"/> (an escaped exception on a raw thread
    /// would terminate the process).
    /// </summary>
    private void PumpWorker(IDatabaseEngineWorker worker, CancellationToken cancellationToken)
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
    /// Stops and joins the self-scheduled worker pumps and releases the engine's
    /// claims, so a stop-then-start cycle (or a host composed later) can claim them
    /// afresh.
    /// </summary>
    private void StopSelfScheduledWorkers()
    {
        var stopSource = _workerStopSource;
        if (stopSource is null)
        {
            return;
        }

        _workerStopSource = null;
        stopSource.Cancel();

        foreach (var (worker, thread) in _selfScheduled)
        {
            thread.Join();
            worker.Release();
        }

        _selfScheduled.Clear();
        stopSource.Dispose();
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
