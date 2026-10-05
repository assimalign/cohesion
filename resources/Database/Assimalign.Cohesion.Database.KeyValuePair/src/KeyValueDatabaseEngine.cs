using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Database.KeyValuePair.Internal;

namespace Assimalign.Cohesion.Database.KeyValuePair;

using Assimalign.Cohesion.Database.KeyValuePair.Storage;
using Assimalign.Cohesion.Database.Storage;

using Internal;

/// <summary>
/// Key-value database engine that manages the lifecycle of key-value database
/// instances.
/// </summary>
/// <remarks>
/// The engine is a data machine: <see cref="Create"/> returns it fully operational —
/// the storage strategy is resolved and the engine-owned background workers
/// (write-ahead-log group-commit flusher, page write-back, checkpointer, version
/// purge, and the index-maintenance stub) are already pumping on dedicated threads
/// the engine spawned — and disposal is its one lifecycle transition: quiesce the
/// workers, flush each database according to its backing's durability policy and
/// close it. Each database is backed
/// by a storage strategy managing two file sets (data and <c>.catalog</c>); the
/// strategy is file-based when <see cref="KeyValueDatabaseEngineOptions.RootPath"/>
/// is set and in-memory otherwise. The journal is owned per-database through the
/// storage layer, so there is no engine-level write-ahead log.
/// </remarks>
public sealed class KeyValueDatabaseEngine : IDatabaseEngine
{
    private readonly KeyValueDatabaseEngineOptions _options;
    private readonly Dictionary<string, IDatabase> _databases = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _syncRoot = new();
    private readonly List<IDatabaseEngineWorker> _workers;
    private readonly List<IDatabaseServer> _servers = [];
    private readonly List<IDatabaseEngineWorker> _customWorkers = [];
    private readonly ManualResetEventSlim _commitPendingSignal = new();
    private readonly Action _signalCommitPending;

    // Runs each database's checkpoint on a lane of its own; its lanes stop after the pumps.
    private readonly KeyValueCheckpointWorker _checkpointWorker;
    private readonly List<Thread> _workerThreads = new();
    private readonly CancellationTokenSource _workerStopSource = new();
    private readonly IKeyValueStorageStrategy _strategy;

    // Woken by a storage whose journal reached the checkpoint size, and by a coordinator that
    // deferred an undo, so the checkpoint and version-purge workers act at once (#1254, #1226).
    private readonly ManualResetEventSlim _checkpointNeededSignal = new();
    private readonly ManualResetEventSlim _undoDeferredSignal = new();
    private readonly int _bufferPoolPages;

    private KeyValueStorage[] _storageSnapshot = [];
    private KeyValueDatabaseInstance[] _instanceSnapshot = [];

    // The worker inventory as published to readers (Workers, State): replaced whole whenever a
    // worker is attached, so a reader never enumerates a list being changed.
    private IReadOnlyList<IDatabaseEngineWorker> _workerView = [];

    // The last exception that escaped a worker's Run (only a worker that does not derive from
    // DatabaseEngineWorker can let one escape); its pump restarted it, and the engine reports
    // Faulted until it is disposed, because it cannot tell when such a worker is healthy again.
    private Exception? _workerRunFault;
    private bool _disposed;

    /// <summary>
    /// The storage-name suffix of the dedicated catalog file set each database owns.
    /// </summary>
    internal const string CatalogSuffix = ".catalog";

    /// <summary>
    /// The buffer pool of a catalog file set, in pages (1 MiB): a catalog holds index
    /// registrations and the format marker, a handful of pages, so
    /// <see cref="KeyValueDatabaseEngineOptions.BufferPoolCapacity"/> sizes the data file set alone.
    /// </summary>
    internal const int CatalogBufferPoolPages = 128;

    private KeyValueDatabaseEngine(KeyValueDatabaseEngineOptions options)
    {
        _options = options;
        Name = options.EngineName ?? "keyvalue-engine";
        _signalCommitPending = _commitPendingSignal.Set;
        _bufferPoolPages = Assimalign.Cohesion.Database.Storage.Storage.GetBufferPoolPageCount(options.BufferPoolCapacity, nameof(options.BufferPoolCapacity));

        // Resolve the storage strategy at creation: the engine is operational from
        // the moment the constructor returns (create → use → dispose; no start).
        _strategy = options.StorageStrategy
            ?? (options.RootPath is { IsEmpty: false } strategyRoot
                ? new FileSystemKeyValueStorageStrategy(strategyRoot, options.Durability)
                : new InMemoryKeyValueStorageStrategy(options.Durability));

        if (options.RootPath is { IsEmpty: false } root)
        {
            Directory.CreateDirectory(root);
        }

        _checkpointWorker = new KeyValueCheckpointWorker(this);
        _workers =
        [
            new KeyValueWriteAheadFlushWorker(this, _commitPendingSignal),
            new KeyValuePageWriteBackWorker(this),
            _checkpointWorker,
            new KeyValueVersionPurgeWorker(this),
            new KeyValueIndexMaintenanceWorker(this),
        ];
        PublishWorkers();

        // Spawn the worker pumps last, after every field they observe is
        // initialized: one dedicated background thread per worker, alive until
        // disposal. Embedded and hosted consumers get identical durability
        // behavior because nothing outside the engine participates (R10).
        StartWorkerThreads();
    }

    /// <inheritdoc />
    public string Name { get; }

    /// <inheritdoc />
    /// <remarks>
    /// <see cref="EngineState.Faulted"/> while one of the engine's workers keeps failing: its
    /// <see cref="DatabaseEngineWorker.Fault"/> is set by a failure until a pass finishes the work it
    /// left (#1268). An offline database is not a worker failure; <see cref="OfflineDatabases"/>
    /// lists it.
    /// </remarks>
    public EngineState State
        => DatabaseEngineWorkerPump.Fold(_disposed, Volatile.Read(ref _workerRunFault),
            Volatile.Read(ref _workerView));

    /// <inheritdoc />
    public IReadOnlyList<DatabaseName> OfflineDatabases
    {
        get
        {
            List<DatabaseName>? offline = null;
            foreach (var database in GetInstanceSnapshot())
            {
                if (database.IsOffline)
                {
                    (offline ??= []).Add(database.Name);
                }
            }

            return offline is null ? [] : offline.AsReadOnly();
        }
    }

    /// <inheritdoc />
    public EngineModel Model => EngineModel.KeyValueStore;

    /// <inheritdoc />
    public IReadOnlyList<IDatabaseEngineWorker> Workers => Volatile.Read(ref _workerView);

    /// <inheritdoc />
    public IReadOnlyList<IDatabaseServer> Servers => _servers.AsReadOnly();

    /// <summary>
    /// Gets the engine options, for the engine's background workers.
    /// </summary>
    internal KeyValueDatabaseEngineOptions EngineOptions => _options;

    /// <summary>
    /// Gets a point-in-time snapshot of every open storage file set (the data and
    /// catalog sets of every open database), for the engine's background workers.
    /// The snapshot is rebuilt when databases open or close; a worker pass may
    /// therefore race a drop, which workers tolerate.
    /// </summary>
    internal KeyValueStorage[] GetStorageSnapshot() => Volatile.Read(ref _storageSnapshot);

    /// <summary>
    /// Gets a point-in-time snapshot of every open database instance, for
    /// workers that operate through the per-database transaction coordinator
    /// (checkpointing, version purge). Same racing-a-drop tolerance as
    /// <see cref="GetStorageSnapshot"/>.
    /// </summary>
    internal KeyValueDatabaseInstance[] GetInstanceSnapshot() => Volatile.Read(ref _instanceSnapshot);

    /// <summary>
    /// Gets the signal a storage sets when its journal reaches
    /// <see cref="KeyValueDatabaseEngineOptions.CheckpointJournalSize"/>; the checkpoint worker waits on it.
    /// </summary>
    internal ManualResetEventSlim CheckpointNeededSignal => _checkpointNeededSignal;

    /// <summary>
    /// Gets the signal a database's coordinator sets when it defers an undo; the version-purge
    /// worker waits on it so the first retry runs about 100 ms later, not a maintenance interval.
    /// </summary>
    internal ManualResetEventSlim UndoDeferredSignal => _undoDeferredSignal;

    /// <summary>
    /// Reports whether <paramref name="database"/> is still one of the engine's open databases:
    /// false once it was dropped, closed for a reopen, or the engine closed it. A worker pass that
    /// raced such a close tolerates the <see cref="ObjectDisposedException"/> it gets; one from a
    /// database still open is a failure.
    /// </summary>
    /// <param name="database">The database a worker pass visited.</param>
    internal bool IsOpen(KeyValueDatabaseInstance database) => Array.IndexOf(GetInstanceSnapshot(), database) >= 0;

    /// <summary>
    /// Creates a new key-value database engine from options. The engine is
    /// operational — background workers running — when this method returns.
    /// </summary>
    /// <param name="options">Engine creation options.</param>
    /// <returns>A new engine instance.</returns>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <see cref="KeyValueDatabaseEngineOptions.BufferPoolCapacity"/> is not a whole number of 8 KiB
    /// pages of at least 1 MiB; <see cref="KeyValueDatabaseEngineOptions.CheckpointJournalSize"/> is
    /// negative; or <see cref="KeyValueDatabaseEngineOptions.CheckpointInterval"/> or
    /// <see cref="KeyValueDatabaseEngineOptions.MaintenanceInterval"/> is not positive.
    /// </exception>
    public static KeyValueDatabaseEngine Create(KeyValueDatabaseEngineOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        // Checked before the constructor spawns the worker threads.
        Assimalign.Cohesion.Database.Storage.Storage.GetBufferPoolPageCount(options.BufferPoolCapacity, nameof(options.BufferPoolCapacity));
        ArgumentOutOfRangeException.ThrowIfNegative(options.CheckpointJournalSize, nameof(options.CheckpointJournalSize));
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(options.CheckpointInterval, TimeSpan.Zero, nameof(options.CheckpointInterval));
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(options.MaintenanceInterval, TimeSpan.Zero, nameof(options.MaintenanceInterval));
        return new KeyValueDatabaseEngine(options);
    }

    /// <summary>Creates a dependency-free builder for key-value options and nested worker/server factories.</summary>
    /// <returns>A fresh builder supporting one engine construction attempt.</returns>
    public static IKeyValueDatabaseEngineBuilder CreateBuilder() => new KeyValueDatabaseEngineBuilder();

    /// <inheritdoc />
    public ValueTask<IDatabase> CreateDatabaseAsync(DatabaseName name, CancellationToken cancellationToken = default)
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

            var storage = _strategy.CreateStorage(name);
            KeyValueStorage? catalogStorage = null;

            // Publish the storages to the worker snapshot BEFORE constructing the
            // instance: instance construction itself commits (the primary-index
            // bootstrap, the format marker), and under grouped durability those
            // commits need the flush worker to see the storages or they wait out
            // the whole self-help window.
            try
            {
                ConfigureStorage(storage, name, catalog: false);
                catalogStorage = _strategy.CreateStorage(name + CatalogSuffix);
                ConfigureStorage(catalogStorage, name + CatalogSuffix, catalog: true);
                PublishStorageSnapshotLocked(storage, catalogStorage);
                var database = new KeyValueDatabaseInstance(name, this, storage, catalogStorage);
                _databases[name] = database;
                return new ValueTask<IDatabase>(database);
            }
            catch
            {
                try
                {
                    storage.Dispose();
                }
                finally
                {
                    catalogStorage?.Dispose();
                }
                throw;
            }
            finally
            {
                RebuildStorageSnapshotLocked();
            }
        }
    }

    /// <inheritdoc />
    public ValueTask<IDatabase> OpenDatabaseAsync(DatabaseName name, CancellationToken cancellationToken = default)
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
                if (existing is not KeyValueDatabaseInstance { IsOffline: true } offline)
                {
                    return new ValueTask<IDatabase>(existing);
                }

                // The database went offline after a failed durable flush (#1243): reopening it
                // is the one way back. Its close writes nothing, and the open below runs
                // recovery, which decides the outcome of every commit that was not confirmed.
                _databases.Remove(name);
                RebuildStorageSnapshotLocked();
                offline.Dispose();
            }

            if (!_strategy.StorageExists(name))
            {
                throw new DatabaseNotFoundException($"Database '{name}' does not exist.");
            }

            KeyValueStorage storage;
            try
            {
                storage = _strategy.OpenStorage(name);
            }
            catch (StorageFormatException exception)
            {
                throw RefuseStorageFormat(name, "data", name, exception);
            }

            KeyValueStorage? catalogStorage = null;

            // See CreateDatabaseAsync: instance construction commits (recovery
            // checkpoint, primary-index re-attachment), so the flush worker must
            // see the storages first under grouped durability.
            try
            {
                ConfigureStorage(storage, name, catalog: false);
                try
                {
                    catalogStorage = _strategy.StorageExists(name + CatalogSuffix)
                        ? _strategy.OpenStorage(name + CatalogSuffix)
                        : _strategy.CreateStorage(name + CatalogSuffix);
                }
                catch (StorageFormatException exception)
                {
                    throw RefuseStorageFormat(name, "catalog", name + CatalogSuffix, exception);
                }

                ConfigureStorage(catalogStorage, name + CatalogSuffix, catalog: true);
                PublishStorageSnapshotLocked(storage, catalogStorage);
                var database = new KeyValueDatabaseInstance(name, this, storage, catalogStorage, recover: true);
                _databases[name] = database;
                return new ValueTask<IDatabase>(database);
            }
            catch
            {
                try
                {
                    storage.Dispose();
                }
                finally
                {
                    catalogStorage?.Dispose();
                }
                throw;
            }
            finally
            {
                RebuildStorageSnapshotLocked();
            }
        }
    }

    /// <summary>
    /// Names the database and the file set behind a storage format refusal; the refusal itself
    /// names the formats found and supported, and the remedy.
    /// </summary>
    /// <param name="name">The database.</param>
    /// <param name="role">Which of the database's file sets was refused: <c>data</c> or <c>catalog</c>.</param>
    /// <param name="storageName">The file set's storage name.</param>
    /// <param name="exception">The storage's refusal.</param>
    private static DatabaseException RefuseStorageFormat(string name, string role, string storageName, StorageFormatException exception)
        => new($"Database '{name}' cannot be opened: its {role} file set '{storageName}' was refused. {exception.Message}", exception);

    /// <inheritdoc />
    public ValueTask DropDatabaseAsync(DatabaseName name, CancellationToken cancellationToken = default)
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
    public bool TryGetDatabase(DatabaseName name, out IDatabase database)
    {
        ThrowIfDisposed();

        lock (_syncRoot)
        {
            return _databases.TryGetValue(name, out database!);
        }
    }

    internal void AttachWorker(IDatabaseEngineWorker worker)
    {
        ThrowIfDisposed();
        if (string.IsNullOrWhiteSpace(worker.Name))
        {
            throw new ArgumentException("A worker must have a diagnostic name.", nameof(worker));
        }
        foreach (var existing in _workers)
        {
            if (string.Equals(existing.Name, worker.Name, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException($"Worker name '{worker.Name}' is already registered.");
            }
        }
        var thread = new Thread(() => DatabaseEngineWorkerPump.Pump(worker, _workerStopSource.Token, ref _workerRunFault))
        {
            IsBackground = true,
            Name = worker.Name,
        };
        _workers.Add(worker);
        _customWorkers.Add(worker);
        _workerThreads.Add(thread);
        try
        {
            thread.Start();
        }
        catch (Exception failure) when (failure is not OutOfMemoryException)
        {
            _workers.Remove(worker);
            _customWorkers.Remove(worker);
            _workerThreads.Remove(thread);
            throw;
        }

        PublishWorkers();
    }

    /// <summary>
    /// Publishes the worker inventory to readers as a fresh read-only copy.
    /// </summary>
    private void PublishWorkers() => Volatile.Write(ref _workerView, Array.AsReadOnly(_workers.ToArray()));

    internal void AttachServer(IDatabaseServer server)
    {
        ThrowIfDisposed();
        _servers.Add(server);
    }
    /// <inheritdoc />
    public void Dispose() => Task.Run(async () => await DisposeAsync().ConfigureAwait(false)).GetAwaiter().GetResult();

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        List<Exception> failures = [];

        // Servers release listeners and active sessions before engine workers or
        // database storage disappear. Continue cleanup after independent failures.
        for (int index = _servers.Count - 1; index >= 0; index--)
        {
            try
            {
                await _servers[index].DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception failure) when (failure is not OutOfMemoryException)
            {
                failures.Add(failure);
            }
        }

        try
        {
            StopWorkerThreads();
        }
        catch (Exception failure) when (failure is not OutOfMemoryException)
        {
            failures.Add(failure);
        }

        try
        {
            // A checkpoint the worker left running on its lane ends before the storages close.
            _checkpointWorker.Dispose();
        }
        catch (Exception failure) when (failure is not OutOfMemoryException)
        {
            failures.Add(failure);
        }

        for (int index = _customWorkers.Count - 1; index >= 0; index--)
        {
            try
            {
                if (_customWorkers[index] is IAsyncDisposable asyncDisposable)
                {
                    await asyncDisposable.DisposeAsync().ConfigureAwait(false);
                }
                else if (_customWorkers[index] is IDisposable disposable)
                {
                    disposable.Dispose();
                }
            }
            catch (Exception failure) when (failure is not OutOfMemoryException)
            {
                failures.Add(failure);
            }
        }

        IDatabase[] snapshot;
        lock (_syncRoot)
        {
            snapshot = [.. _databases.Values];
            _databases.Clear();
            _storageSnapshot = [];
            _instanceSnapshot = [];
        }
        foreach (var database in snapshot)
        {
            try
            {
                await database.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception failure) when (failure is not OutOfMemoryException)
            {
                failures.Add(failure);
            }
        }
        _workerStopSource.Dispose();
        _commitPendingSignal.Dispose();
        _checkpointNeededSignal.Dispose();
        _undoDeferredSignal.Dispose();
        if (failures.Count != 0)
        {
            throw new AggregateException("Engine disposal encountered failures.", failures);
        }
    }
    /// <summary>
    /// Configures a freshly created or opened storage file set with the engine's
    /// durability policy, its buffer pool capacity and checkpoint size (#1254), and wires
    /// its commit-pending and checkpoint-needed hooks to the engine's worker signals.
    /// </summary>
    /// <remarks>
    /// The engine sets the pool on whatever storage its strategy returns, so a custom
    /// <see cref="IKeyValueStorageStrategy"/> needs no knowledge of the option.
    /// </remarks>
    private void ConfigureStorage(KeyValueStorage storage, string storageName, bool catalog)
    {
        storage.ConfigureCommitDurability(_options.Durability, $"{_strategy.GetType().Name} ({storageName})");
        storage.GroupCommitWindow = _options.GroupCommitWindow;
        storage.OnCommitPending = _signalCommitPending;
        storage.BufferPoolCapacity = catalog ? CatalogBufferPoolPages : _bufferPoolPages;
        storage.CheckpointJournalSize = _options.CheckpointJournalSize;
        storage.OnCheckpointNeeded = _checkpointNeededSignal.Set;
    }

    /// <summary>
    /// Publishes a provisional storage snapshot containing the open databases'
    /// storages plus the given not-yet-registered ones — called before instance
    /// construction so the flush/write-back workers can serve commits the
    /// construction itself performs.
    /// </summary>
    private void PublishStorageSnapshotLocked(KeyValueStorage storage, KeyValueStorage catalogStorage)
    {
        var current = _storageSnapshot;
        var storages = new KeyValueStorage[current.Length + 2];
        current.CopyTo(storages, 0);
        storages[^2] = storage;
        storages[^1] = catalogStorage;
        Volatile.Write(ref _storageSnapshot, storages);
    }

    /// <summary>
    /// Rebuilds the storage snapshot the background workers iterate. Called under
    /// the engine lock whenever the open-database set changes.
    /// </summary>
    private void RebuildStorageSnapshotLocked()
    {
        var storages = new KeyValueStorage[_databases.Count * 2];
        var instances = new KeyValueDatabaseInstance[_databases.Count];
        int index = 0;
        int instanceIndex = 0;

        foreach (var database in _databases.Values)
        {
            var instance = (KeyValueDatabaseInstance)database;
            storages[index++] = instance.DataStorage;
            storages[index++] = instance.CatalogStorage;
            instances[instanceIndex++] = instance;
        }

        Volatile.Write(ref _storageSnapshot, storages);
        Volatile.Write(ref _instanceSnapshot, instances);
    }

    /// <summary>
    /// Spawns one dedicated background thread per worker at engine creation. The
    /// engine owns every loop; there is no external scheduler.
    /// </summary>
    private void StartWorkerThreads()
    {
        foreach (var worker in _workers)
        {
            var thread = new Thread(() => DatabaseEngineWorkerPump.Pump(worker, _workerStopSource.Token, ref _workerRunFault))
            {
                IsBackground = true,
                Name = worker.Name,
            };

            _workerThreads.Add(thread);
            thread.Start();
        }
    }

    /// <summary>
    /// Signals and joins the worker pumps (disposal path).
    /// </summary>
    private void StopWorkerThreads()
    {
        List<Exception> failures = [];
        try
        {
            _workerStopSource.Cancel();
        }
        catch (Exception failure) when (failure is not OutOfMemoryException)
        {
            // A custom cancellation callback must not skip worker joins.
            failures.Add(failure);
        }
        foreach (var thread in _workerThreads)
        {
            try
            {
                thread.Join();
            }
            catch (Exception failure) when (failure is not OutOfMemoryException)
            {
                failures.Add(failure);
            }
        }
        _workerThreads.Clear();
        if (failures.Count != 0)
        {
            throw new AggregateException("Worker shutdown encountered failures.", failures);
        }
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }
}
