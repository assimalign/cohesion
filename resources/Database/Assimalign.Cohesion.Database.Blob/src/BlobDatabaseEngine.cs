using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Database.Blob.Internal;
using Assimalign.Cohesion.Database.Blob.Storage;
using Assimalign.Cohesion.Database.Storage;

namespace Assimalign.Cohesion.Database.Blob;

/// <summary>Manages logical blob databases and their four maintenance workers.</summary>
/// <remarks>Creation starts the workers; disposal stops them and closes every database according to its storage durability.</remarks>
public sealed class BlobDatabaseEngine : IDatabaseEngine
{
    private readonly BlobDatabaseEngineOptions _options;
    private readonly Dictionary<string, BlobDatabaseInstance> _databases = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _sync = new();
    private readonly ManualResetEventSlim _commitPending = new();

    // Woken by a storage whose journal reached the checkpoint size, and by a coordinator that
    // deferred an undo, so the checkpoint and version-purge workers act at once (#1254, #1226).
    private readonly ManualResetEventSlim _checkpointNeeded = new();
    private readonly ManualResetEventSlim _undoDeferred = new();
    private readonly CancellationTokenSource _stop = new();
    private readonly List<IDatabaseEngineWorker> _workers = [];
    private readonly List<IDatabaseServer> _servers = [];
    private readonly List<Thread> _threads = [];
    private readonly string? _rootPath;
    private BlobDatabaseInstance[] _instances = [];
    private BlobStorage[] _storages = [];
    private Exception? _workerFault;
    private int _disposed;

    private BlobDatabaseEngine(BlobDatabaseEngineOptions options)
    {
        _options = options;
        Name = options.EngineName ?? "blob-engine";
        _rootPath = options.StorageStrategy is null && options.RootPath is { IsEmpty: false } root ? Path.GetFullPath(root) : null;
        if (_rootPath is not null)
        {
            Directory.CreateDirectory(_rootPath);
        }

        AttachWorker(new BlobWriteAheadFlushWorker(this, _commitPending));
        AttachWorker(new BlobPageWriteBackWorker(this));
        AttachWorker(new BlobCheckpointWorker(this));
        AttachWorker(new BlobVersionPurgeWorker(this));
    }

    /// <inheritdoc />
    public string Name { get; }
    /// <inheritdoc />
    public EngineState State => Volatile.Read(ref _disposed) != 0 ? EngineState.Disposed :
        Volatile.Read(ref _workerFault) is null ? EngineState.Running : EngineState.Faulted;
    /// <inheritdoc />
    public EngineModel Model => EngineModel.Blob;
    /// <inheritdoc />
    public IReadOnlyList<IDatabaseEngineWorker> Workers => _workers.AsReadOnly();
    /// <inheritdoc />
    public IReadOnlyList<IDatabaseServer> Servers => _servers.AsReadOnly();
    internal BlobDatabaseEngineOptions EngineOptions => _options;
    internal BlobStorage[] GetStorageSnapshot() => Volatile.Read(ref _storages);
    internal BlobDatabaseInstance[] GetInstanceSnapshot() => Volatile.Read(ref _instances);

    /// <summary>
    /// Gets the signal a storage sets when its journal reaches
    /// <see cref="BlobDatabaseEngineOptions.CheckpointJournalSize"/>; the checkpoint worker waits on it.
    /// </summary>
    internal ManualResetEventSlim CheckpointNeededSignal => _checkpointNeeded;

    /// <summary>
    /// Gets the signal a database's coordinator sets when it defers an undo; the version-purge
    /// worker waits on it so the first retry runs about 100 ms later, not a maintenance interval.
    /// </summary>
    internal ManualResetEventSlim UndoDeferredSignal => _undoDeferred;

    /// <summary>
    /// Records a background worker's failure without stopping the worker: the engine reports
    /// <see cref="EngineState.Faulted"/> and the worker's next pass runs as usual.
    /// </summary>
    /// <param name="exception">The failure.</param>
    internal void ReportWorkerFault(Exception exception) => Interlocked.CompareExchange(ref _workerFault, exception, null);

    /// <summary>Creates a dependency-free builder for an engine and its deferred workers and servers.</summary>
    /// <returns>A one-shot model builder; constructing the builder starts no components.</returns>
    /// <remarks>Use this entry point inside hosting-aware factories to assign already resolved values before Build.</remarks>
    public static IBlobDatabaseEngineBuilder CreateBuilder() => new BlobDatabaseEngineBuilder();

    /// <summary>Creates an operational engine using memory or files under the configured root.</summary>
    /// <param name="options">The engine configuration.</param>
    /// <returns>The running engine.</returns>
    /// <exception cref="ArgumentNullException">The options are null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// A worker interval or batch size is not positive, the buffer pool capacity is not a whole
    /// number of 8 KiB pages of at least 1 MiB, or the checkpoint journal size is negative.
    /// </exception>
    public static BlobDatabaseEngine Create(BlobDatabaseEngineOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(options.CheckpointInterval, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(options.PageWriteBackInterval, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(options.MaintenanceInterval, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(options.GroupCommitWindow, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(options.PageWriteBackBatchSize);
        Assimalign.Cohesion.Database.Storage.Storage.GetBufferPoolPageCount(options.BufferPoolCapacity, nameof(options.BufferPoolCapacity));
        ArgumentOutOfRangeException.ThrowIfNegative(options.CheckpointJournalSize, nameof(options.CheckpointJournalSize));
        return new BlobDatabaseEngine(options);
    }

    /// <inheritdoc />
    public ValueTask<IDatabase> CreateDatabaseAsync(DatabaseName name, CancellationToken cancellationToken = default)
        => GetDatabase(name, create: true, cancellationToken);

    /// <inheritdoc />
    public ValueTask<IDatabase> OpenDatabaseAsync(DatabaseName name, CancellationToken cancellationToken = default)
        => GetDatabase(name, create: false, cancellationToken);

    private ValueTask<IDatabase> GetDatabase(string name, bool create, CancellationToken cancellationToken)
    {
        ValidateName(name);
        cancellationToken.ThrowIfCancellationRequested();
        lock (_sync)
        {
            ThrowIfDisposed();
            if (_databases.TryGetValue(name, out var existing))
            {
                if (create)
                {
                    throw new DatabaseException($"Database '{name}' already exists.");
                }

                existing.ThrowIfDisposed();
                if (!existing.IsOffline)
                {
                    return new ValueTask<IDatabase>(existing);
                }

                // The database went offline after a failed durable flush (#1243): reopening it
                // is the one way back. Its close writes nothing, and the open below runs
                // recovery, which decides the outcome of every commit that was not confirmed.
                _databases.Remove(name);
                RebuildSnapshot();
                existing.Dispose();
            }
            var directory = FindDirectory(name);
            bool exists = _options.StorageStrategy?.StorageExists(name) ?? directory is not null;
            if (create && exists)
            {
                throw new DatabaseException($"Database '{name}' already exists.");
            }

            if (!create && !exists)
            {
                throw new DatabaseNotFoundException($"Database '{name}' does not exist.");
            }

            if (_rootPath is not null && create)
            {
                directory = Path.Combine(_rootPath, name);
                Directory.CreateDirectory(directory);
            }
            BlobStorage? storage = null;
            try
            {
                storage = _options.StorageStrategy is { } strategy
                    ? create ? strategy.CreateStorage(name, _options.Durability) : strategy.OpenStorage(name, _options.Durability)
                    : OpenStorage(directory, name, create, _options.Durability);
                if (storage is null)
                {
                    throw new InvalidOperationException("The storage strategy returned null.");
                }
                storage.GroupCommitWindow = _options.GroupCommitWindow;
                storage.OnCommitPending = _commitPending.Set;

                // The engine sizes the pool and arms the checkpoint trigger on whatever storage
                // the strategy returned, so a custom strategy needs no knowledge of them (#1254).
                storage.BufferPoolCapacity = Assimalign.Cohesion.Database.Storage.Storage.GetBufferPoolPageCount(_options.BufferPoolCapacity, nameof(_options.BufferPoolCapacity));
                storage.CheckpointJournalSize = _options.CheckpointJournalSize;
                storage.OnCheckpointNeeded = _checkpointNeeded.Set;
                Volatile.Write(ref _storages, [.. _storages, storage]);
                var database = new BlobDatabaseInstance(name, this, storage, recover: !create);
                _databases.Add(name, database);
                return new ValueTask<IDatabase>(database);
            }
            catch (StorageFormatException exception)
            {
                // The storage refused the file set before recovery wrote anything; the refusal
                // names the formats and the remedy, and this names the database.
                storage?.Dispose();
                throw new DatabaseException($"Database '{name}' cannot be opened. {exception.Message}", exception);
            }
            catch
            {
                storage?.Dispose();
                throw;
            }
            finally { RebuildSnapshot(); }
        }
    }

    private static BlobStorage OpenStorage(string? directory, string name, bool create, StorageCommitDurability? durability)
    {
        StorageStream? data = null, journal = null, backup = null;
        try
        {
            var mode = create ? FileMode.CreateNew : FileMode.Open;
            data = directory is null ? StorageStream.FromInMemory() : StorageStream.FromFile(Path.Combine(directory, "blob.dat"), mode, FileShare.Read);
            journal = directory is null ? StorageStream.FromInMemory() : StorageStream.FromFile(Path.Combine(directory, "blob.log"), mode, FileShare.Read);
            backup = directory is null ? StorageStream.FromInMemory() : StorageStream.FromFile(Path.Combine(directory, "blob.bak"), mode, FileShare.Read);
            return create ? BlobStorage.Create(data, journal, backup, name, durability) : BlobStorage.Open(data, journal, backup, checkpointOnOpen: false, durability);
        }
        catch { data?.Dispose(); journal?.Dispose(); backup?.Dispose(); throw; }
    }

    /// <inheritdoc />
    public ValueTask DropDatabaseAsync(DatabaseName name, CancellationToken cancellationToken = default)
    {
        ValidateName(name);
        cancellationToken.ThrowIfCancellationRequested();
        lock (_sync)
        {
            ThrowIfDisposed();
            var directory = FindDirectory(name);
            bool open = _databases.Remove(name, out var database);
            if (!open && !(_options.StorageStrategy?.StorageExists(name) ?? directory is not null))
            {
                throw new DatabaseNotFoundException($"Database '{name}' does not exist.");
            }

            RebuildSnapshot();
            database?.Dispose();
            if (_options.StorageStrategy is { } strategy)
            {
                strategy.DropStorage(name);
            }
            else if (directory is not null)
            {
                Directory.Delete(directory, recursive: true);
            }
        }
        return default;
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<IDatabase> GetDatabasesAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        string[] names;
        lock (_sync)
        {
            ThrowIfDisposed();
            names = _databases.Keys.Concat(_options.StorageStrategy is { } strategy ? strategy.GetDatabaseNames().Select(name => name.ToString()) : _rootPath is null ? [] : Directory.EnumerateDirectories(_rootPath)
                .Where(path => File.Exists(Path.Combine(path, "blob.dat"))).Select(path => Path.GetFileName(path)))
                .Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToArray();
        }
        foreach (string name in names)
        {
            yield return await OpenDatabaseAsync(name, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    public bool TryGetDatabase(DatabaseName name, out IDatabase database)
    {
        ValidateName(name);
        lock (_sync)
        {
            ThrowIfDisposed();
            bool found = _databases.TryGetValue(name, out var instance);
            database = instance!;
            return found;
        }
    }

    private string? FindDirectory(string name) => _rootPath is null ? null : Directory.EnumerateDirectories(_rootPath)
        .FirstOrDefault(path => string.Equals(Path.GetFileName(path), name, StringComparison.OrdinalIgnoreCase));

    private static void ValidateName(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (name is "." or ".." || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || name.Contains('/') || name.Contains('\\'))
        {
            throw new ArgumentException("A database name must be a single file-name component.", nameof(name));
        }
    }

    private void RebuildSnapshot()
    {
        Volatile.Write(ref _instances, [.. _databases.Values]);
        Volatile.Write(ref _storages, _databases.Values.Select(database => database.DataStorage).ToArray());
    }

    internal void AttachWorker(IDatabaseEngineWorker worker)
    {
        ThrowIfDisposed();
        var thread = new Thread(() => Pump(worker)) { IsBackground = true, Name = Name + "/" + worker.Kind };
        _workers.Add(worker);
        _threads.Add(thread);
        try { thread.Start(); }
        catch
        {
            _threads.Remove(thread);
            _workers.Remove(worker);
            throw;
        }
    }

    internal void AttachServer(IDatabaseServer server)
    {
        ThrowIfDisposed();
        _servers.Add(server);
    }

    private void Pump(IDatabaseEngineWorker worker)
    {
        try { worker.Run(_stop.Token); }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
        catch (Exception error) when (error is not OutOfMemoryException) { Interlocked.CompareExchange(ref _workerFault, error, null); }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        List<Exception>? errors = null;
        for (int index = _servers.Count - 1; index >= 0; index--)
        {
            try { Task.Run(async () => await _servers[index].DisposeAsync().ConfigureAwait(false)).GetAwaiter().GetResult(); }
            catch (Exception error) { (errors ??= []).Add(error); }
        }

        try { _stop.Cancel(); }
        catch (Exception error) { (errors ??= []).Add(error); }
        foreach (var thread in _threads)
        {
            thread.Join();
        }

        for (int index = _workers.Count - 1; index >= 0; index--)
        {
            try
            {
                if (_workers[index] is IAsyncDisposable asynchronous)
                {
                    Task.Run(async () => await asynchronous.DisposeAsync().ConfigureAwait(false)).GetAwaiter().GetResult();
                }
                else if (_workers[index] is IDisposable disposable)
                {
                    disposable.Dispose();
                }
            }
            catch (Exception error) { (errors ??= []).Add(error); }
        }

        lock (_sync)
        {
            foreach (var database in _databases.Values)
            {
                try { database.Dispose(); }
                catch (Exception error) { (errors ??= []).Add(error); }
            }
            _databases.Clear();
            RebuildSnapshot();
        }
        _commitPending.Dispose();
        _checkpointNeeded.Dispose();
        _undoDeferred.Dispose();
        _stop.Dispose();
        if (errors is not null)
        {
            throw new AggregateException("One or more blob engine components failed to close.", errors);
        }
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync() { Dispose(); return default; }
    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
}
