using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Database.Documents.Internal;
using Assimalign.Cohesion.Database.Documents.Storage;

namespace Assimalign.Cohesion.Database.Documents;

/// <summary>Manages logical document databases and their four maintenance workers.</summary>
/// <remarks>Creation starts the workers; disposal stops them and durably closes every database.</remarks>
public sealed class DocumentDatabaseEngine : IDatabaseEngine
{
    private readonly DocumentDatabaseEngineOptions _options;
    private readonly Dictionary<string, DocumentDatabaseInstance> _databases = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _sync = new();
    private readonly ManualResetEventSlim _commitPending = new();
    private readonly CancellationTokenSource _stop = new();
    private readonly DatabaseEngineWorker[] _workers;
    private readonly List<Thread> _threads = [];
    private readonly string? _rootPath;
    private DocumentDatabaseInstance[] _instances = [];
    private DocumentStorage[] _storages = [];
    private Exception? _workerFault;
    private int _disposed;

    private DocumentDatabaseEngine(DocumentDatabaseEngineOptions options)
    {
        _options = options;
        Name = options.EngineName ?? "document-engine";
        _rootPath = string.IsNullOrWhiteSpace(options.RootPath) ? null : Path.GetFullPath(options.RootPath);
        if (_rootPath is not null)
        {
            Directory.CreateDirectory(_rootPath);
        }

        _workers = [new DocumentWriteAheadFlushWorker(this, _commitPending), new DocumentPageWriteBackWorker(this),
            new DocumentCheckpointWorker(this), new DocumentVersionPurgeWorker(this)];
        foreach (var worker in _workers)
        {
            var thread = new Thread(() => Pump(worker)) { IsBackground = true, Name = Name + "/" + worker.Kind };
            _threads.Add(thread);
            thread.Start();
        }
    }

    /// <inheritdoc />
    public string Name { get; }
    /// <inheritdoc />
    public EngineState State => Volatile.Read(ref _disposed) != 0 ? EngineState.Disposed :
        Volatile.Read(ref _workerFault) is null ? EngineState.Running : EngineState.Faulted;
    /// <inheritdoc />
    public EngineModel Model => EngineModel.Document;
    /// <inheritdoc />
    public IReadOnlyList<IDatabaseEngineWorker> Workers => _workers;
    internal DocumentDatabaseEngineOptions EngineOptions => _options;
    internal DocumentStorage[] GetStorageSnapshot() => Volatile.Read(ref _storages);
    internal DocumentDatabaseInstance[] GetInstanceSnapshot() => Volatile.Read(ref _instances);

    /// <summary>Creates an operational engine using memory or files under the configured root.</summary>
    /// <param name="options">The engine configuration.</param>
    /// <returns>The running engine.</returns>
    /// <exception cref="ArgumentNullException">The options are null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">A worker interval or batch size is not positive.</exception>
    public static DocumentDatabaseEngine Create(DocumentDatabaseEngineOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(options.CheckpointInterval, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(options.PageWriteBackInterval, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(options.MaintenanceInterval, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(options.GroupCommitWindow, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(options.PageWriteBackBatchSize);
        return new DocumentDatabaseEngine(options);
    }

    /// <inheritdoc />
    public ValueTask<IDatabase> CreateDatabaseAsync(string name, CancellationToken cancellationToken = default)
        => GetDatabase(name, create: true, cancellationToken);

    /// <inheritdoc />
    public ValueTask<IDatabase> OpenDatabaseAsync(string name, CancellationToken cancellationToken = default)
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
                return new ValueTask<IDatabase>(existing);
            }
            var directory = FindDirectory(name);
            if (create && directory is not null)
            {
                throw new DatabaseException($"Database '{name}' already exists.");
            }

            if (!create && directory is null)
            {
                throw new DatabaseNotFoundException($"Database '{name}' does not exist.");
            }

            if (_rootPath is not null && create)
            {
                directory = Path.Combine(_rootPath, name);
                Directory.CreateDirectory(directory);
            }
            DocumentStorage? storage = null;
            try
            {
                storage = OpenStorage(directory, name, create);
                storage.CommitDurability = _options.Durability;
                storage.GroupCommitWindow = _options.GroupCommitWindow;
                storage.OnCommitPending = _commitPending.Set;
                Volatile.Write(ref _storages, [.. _storages, storage]);
                var database = new DocumentDatabaseInstance(name, this, storage, recover: !create);
                _databases.Add(name, database);
                return new ValueTask<IDatabase>(database);
            }
            catch
            {
                storage?.Dispose();
                throw;
            }
            finally { RebuildSnapshot(); }
        }
    }

    private static DocumentStorage OpenStorage(string? directory, string name, bool create)
    {
        Stream? data = null, journal = null, backup = null;
        try
        {
            var mode = create ? FileMode.CreateNew : FileMode.Open;
            data = directory is null ? new MemoryStream() : new FileStream(Path.Combine(directory, "document.dat"), mode, FileAccess.ReadWrite, FileShare.Read);
            journal = directory is null ? new MemoryStream() : new FileStream(Path.Combine(directory, "document.log"), mode, FileAccess.ReadWrite, FileShare.Read);
            backup = directory is null ? new MemoryStream() : new FileStream(Path.Combine(directory, "document.bak"), mode, FileAccess.ReadWrite, FileShare.Read);
            return create ? DocumentStorage.Create(data, journal, backup, name) : DocumentStorage.Open(data, journal, backup, checkpointOnOpen: false);
        }
        catch { data?.Dispose(); journal?.Dispose(); backup?.Dispose(); throw; }
    }

    /// <inheritdoc />
    public ValueTask DropDatabaseAsync(string name, CancellationToken cancellationToken = default)
    {
        ValidateName(name);
        cancellationToken.ThrowIfCancellationRequested();
        lock (_sync)
        {
            ThrowIfDisposed();
            var directory = FindDirectory(name);
            bool open = _databases.Remove(name, out var database);
            if (!open && directory is null)
            {
                throw new DatabaseNotFoundException($"Database '{name}' does not exist.");
            }

            RebuildSnapshot();
            database?.Dispose();
            if (directory is not null)
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
            names = _databases.Keys.Concat(_rootPath is null ? [] : Directory.EnumerateDirectories(_rootPath)
                .Where(path => File.Exists(Path.Combine(path, "document.dat"))).Select(path => Path.GetFileName(path)))
                .Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToArray();
        }
        foreach (string name in names)
        {
            yield return await OpenDatabaseAsync(name, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    public bool TryGetDatabase(string name, out IDatabase database)
    {
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

    private void Pump(DatabaseEngineWorker worker)
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

        _stop.Cancel();
        foreach (var thread in _threads)
        {
            thread.Join();
        }

        List<Exception>? errors = null;
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
        _stop.Dispose();
        if (errors is not null)
        {
            throw new AggregateException("One or more document databases failed to close.", errors);
        }
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync() { Dispose(); return default; }
    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
}
