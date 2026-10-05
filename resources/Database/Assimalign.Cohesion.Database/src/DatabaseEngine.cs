using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Database;

/// <summary>
/// The base of every database engine: a data machine that manages logical databases, owns its
/// background workers and the servers composed beneath it, and is disposed once.
/// </summary>
/// <remarks>
/// <para>
/// <b>An engine is a data machine, not a service.</b> It is operational from creation: a leaf
/// attaches its built-in workers from its constructor (<see cref="AttachWorker"/>), and each one
/// starts pumping on a dedicated background thread the engine owns. Disposal is the one lifecycle
/// transition, in a fixed order the base owns: the servers composed beneath the engine are disposed
/// (last attached first), the worker pumps are stopped and joined, the workers are disposed (last
/// attached first), and then the leaf closes its databases (<see cref="DisposeAsyncCore"/>). Every
/// step runs whatever an earlier one threw, and the failures are reported together. Disposal is
/// idempotent.
/// </para>
/// <para>
/// <b>Composition is attached, then frozen.</b> A leaf's internal build path attaches the workers
/// and servers composed for it (<see cref="AttachWorker"/>, <see cref="AttachServer"/>) and then
/// calls <see cref="CompleteComposition"/>; an attach after that, or after disposal, throws. The
/// products belong to the engine from the moment they are attached. A server must front this
/// engine, and no product is attached twice.
/// </para>
/// <para>
/// <b>State.</b> <see cref="State"/> is folded from the engine's own life and its workers (#1268):
/// <see cref="EngineState.Disposed"/> once disposal started, <see cref="EngineState.Faulted"/>
/// while a worker holds a failure it has not worked off (<see cref="DatabaseEngineWorker.Fault"/>)
/// or a worker's loop escaped, and <see cref="EngineState.Running"/> otherwise. An offline database
/// is not a worker failure; <see cref="OfflineDatabases"/> lists it.
/// </para>
/// <para>
/// <b>Shape (concrete-types plan, phase 3, #1259).</b> Every public member is non-virtual and owns
/// the argument, disposed and cancellation checks before it calls a protected core; the one
/// abstract public member is <see cref="OfflineDatabases"/>, state the leaf computes. The leaves
/// live in the model assemblies, so the constructor is <c>protected</c>. A leaf re-exposes its
/// typed database with <c>new</c> members that await the public members here, never the cores.
/// Until phase 6 the base also implements <see cref="IDatabaseEngine"/>, so the hosting layer keeps
/// composing engines through the interface.
/// </para>
/// </remarks>
// Deviates from the repo interface-first rule per design decision: Database engines are concrete-first — abstract bases with protected cores and sealed model leaves (owner, 2026-10-04; database-area.md).
public abstract class DatabaseEngine : IDatabaseEngine
{
    private readonly string _name;
    private readonly EngineModel _model;
    private readonly object _sync = new();
    private readonly List<DatabaseEngineWorker> _workers = [];
    private readonly List<DatabaseServer> _servers = [];
    private readonly List<Thread> _threads = [];
    private readonly CancellationTokenSource _stop = new();

    // The inventories as published to readers: replaced whole on every attach, so a reader never
    // enumerates a list being changed.
    private IReadOnlyList<DatabaseEngineWorker> _workerView = [];
    private IReadOnlyList<DatabaseServer> _serverView = [];

    // The last exception that escaped a worker's Run: its pump ran it again after the backoff, and
    // the engine reports Faulted until it is disposed, because it cannot tell when such a worker
    // is healthy again. DatabaseEngineWorker's loop lets nothing escape, so this stays null for
    // every worker the base can attach; the frame is kept so the pump never ends early.
    private Exception? _workerRunFault;
    private bool _composed;
    private int _disposed;

    /// <summary>
    /// Initializes a new engine with its name and data model.
    /// </summary>
    /// <param name="name">The logical name of the engine instance.</param>
    /// <param name="model">The data model the engine implements.</param>
    /// <exception cref="ArgumentException"><paramref name="name"/> is null, empty or white space.</exception>
    protected DatabaseEngine(string name, EngineModel model)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        _name = name;
        _model = model;
    }

    /// <summary>
    /// Gets the logical name of this engine instance.
    /// </summary>
    public string Name => _name;

    /// <summary>
    /// Gets the data model this engine implements.
    /// </summary>
    public EngineModel Model => _model;

    /// <summary>
    /// Gets the observational state of the engine: <see cref="EngineState.Running"/> from creation,
    /// <see cref="EngineState.Faulted"/> while a background worker keeps failing (the engine keeps
    /// serving), and <see cref="EngineState.Disposed"/> once disposal started.
    /// </summary>
    public EngineState State
    {
        get
        {
            if (Volatile.Read(ref _disposed) != 0)
            {
                return EngineState.Disposed;
            }

            if (Volatile.Read(ref _workerRunFault) is not null)
            {
                return EngineState.Faulted;
            }

            var workers = Volatile.Read(ref _workerView);
            for (int index = 0; index < workers.Count; index++)
            {
                if (workers[index].Fault is not null)
                {
                    return EngineState.Faulted;
                }
            }

            return EngineState.Running;
        }
    }

    /// <summary>
    /// Gets the engine-owned background workers, in the order they were attached: a point-in-time
    /// snapshot, for observability.
    /// </summary>
    public IReadOnlyList<DatabaseEngineWorker> Workers => Volatile.Read(ref _workerView);

    /// <summary>
    /// Gets the servers composed beneath this engine, in the order they were attached. The engine
    /// owns their disposal; an application snapshots the list and drives start and stop.
    /// </summary>
    public IReadOnlyList<DatabaseServer> Servers => Volatile.Read(ref _serverView);

    /// <summary>
    /// Gets the names of the open databases that are offline: a durable flush, a journal drain or a
    /// file header write of theirs failed, and every operation on them is refused until
    /// <see cref="OpenDatabaseAsync"/> reopens them. Empty while every open database is online; a
    /// point-in-time snapshot.
    /// </summary>
    /// <remarks>
    /// State the leaf computes from its storages, so the one abstract public member (rule 4 of
    /// <c>database-area.md</c>). An offline database does not change <see cref="State"/>.
    /// </remarks>
    public abstract IReadOnlyList<DatabaseName> OfflineDatabases { get; }

    /// <summary>
    /// Gets whether disposal of the engine has started.
    /// </summary>
    protected bool IsDisposed => Volatile.Read(ref _disposed) != 0;

    /// <summary>
    /// Creates a new logical database with the specified name.
    /// </summary>
    /// <param name="name">The name of the database to create.</param>
    /// <param name="cancellationToken">Observed before the database is created.</param>
    /// <returns>The newly created database.</returns>
    /// <exception cref="ArgumentException"><paramref name="name"/> is empty.</exception>
    /// <exception cref="ObjectDisposedException">The engine has been disposed.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was canceled before the core ran.</exception>
    /// <exception cref="DatabaseException">A database with the same name already exists.</exception>
    public ValueTask<DatabaseInstance> CreateDatabaseAsync(DatabaseName name, CancellationToken cancellationToken = default)
    {
        ThrowIfEmpty(name);
        ThrowIfDisposed();
        cancellationToken.ThrowIfCancellationRequested();
        return CreateDatabaseCoreAsync(name, cancellationToken);
    }

    /// <summary>
    /// Opens an existing logical database by name. A database that went offline is reopened: the
    /// returned instance is a new one, opened again from its files.
    /// </summary>
    /// <param name="name">The name of the database to open.</param>
    /// <param name="cancellationToken">Observed before the database is opened.</param>
    /// <returns>The opened database.</returns>
    /// <exception cref="ArgumentException"><paramref name="name"/> is empty.</exception>
    /// <exception cref="ObjectDisposedException">The engine has been disposed.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was canceled before the core ran.</exception>
    /// <exception cref="DatabaseNotFoundException">The database does not exist.</exception>
    public ValueTask<DatabaseInstance> OpenDatabaseAsync(DatabaseName name, CancellationToken cancellationToken = default)
    {
        ThrowIfEmpty(name);
        ThrowIfDisposed();
        cancellationToken.ThrowIfCancellationRequested();
        return OpenDatabaseCoreAsync(name, cancellationToken);
    }

    /// <summary>
    /// Drops an existing logical database and its storage.
    /// </summary>
    /// <param name="name">The name of the database to drop.</param>
    /// <param name="cancellationToken">Observed before the database is dropped.</param>
    /// <returns>A task that completes once the database is dropped.</returns>
    /// <exception cref="ArgumentException"><paramref name="name"/> is empty.</exception>
    /// <exception cref="ObjectDisposedException">The engine has been disposed.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was canceled before the core ran.</exception>
    public ValueTask DropDatabaseAsync(DatabaseName name, CancellationToken cancellationToken = default)
    {
        ThrowIfEmpty(name);
        ThrowIfDisposed();
        cancellationToken.ThrowIfCancellationRequested();
        return DropDatabaseCoreAsync(name, cancellationToken);
    }

    /// <summary>
    /// Enumerates the logical databases the engine manages. The engine state and the token are
    /// checked when the call is made, not when the enumeration starts.
    /// </summary>
    /// <param name="cancellationToken">Cancels the enumeration.</param>
    /// <returns>An async sequence of the databases.</returns>
    /// <exception cref="ObjectDisposedException">The engine has been disposed when the call is made.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was canceled when the call is made.</exception>
    public IAsyncEnumerable<DatabaseInstance> GetDatabasesAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        cancellationToken.ThrowIfCancellationRequested();
        return GetDatabasesCore(cancellationToken);
    }

    /// <summary>
    /// Attempts to retrieve an open database by name without throwing when it is not open.
    /// </summary>
    /// <param name="name">The name of the database.</param>
    /// <param name="database">When this method returns true, the database.</param>
    /// <returns>True when the database is open in the engine; otherwise false.</returns>
    /// <exception cref="ArgumentException"><paramref name="name"/> is empty.</exception>
    /// <exception cref="ObjectDisposedException">The engine has been disposed.</exception>
    public bool TryGetDatabase(DatabaseName name, [MaybeNullWhen(false)] out DatabaseInstance database)
    {
        ThrowIfEmpty(name);
        ThrowIfDisposed();
        return TryGetDatabaseCore(name, out database);
    }

    /// <summary>
    /// Disposes the engine: its servers, then its workers' pumps and the workers, then the leaf's
    /// databases. Idempotent; a call made while disposal runs returns at once.
    /// </summary>
    /// <exception cref="AggregateException">One or more components failed to close; every step ran.</exception>
    public void Dispose() => Task.Run(async () => await DisposeAsync().ConfigureAwait(false)).GetAwaiter().GetResult();

    /// <summary>
    /// Disposes the engine: its servers (last attached first), then its workers' pumps, then the
    /// workers (last attached first), then the leaf's databases (<see cref="DisposeAsyncCore"/>).
    /// Work committed before disposal is durable when the task completes. Idempotent; a call made
    /// while disposal runs returns at once.
    /// </summary>
    /// <returns>A task that completes once the engine is disposed.</returns>
    /// <exception cref="AggregateException">One or more components failed to close; every step ran.</exception>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        DatabaseServer[] servers;
        DatabaseEngineWorker[] workers;
        Thread[] threads;
        lock (_sync)
        {
            servers = [.. _servers];
            workers = [.. _workers];
            threads = [.. _threads];
        }

        List<Exception>? failures = null;

        // Servers release their listeners and sessions before the workers or the storages go.
        for (int index = servers.Length - 1; index >= 0; index--)
        {
            try
            {
                await servers[index].DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception failure) when (failure is not OutOfMemoryException)
            {
                (failures ??= []).Add(failure);
            }
        }

        try
        {
            _stop.Cancel();
        }
        catch (Exception failure) when (failure is not OutOfMemoryException)
        {
            // A cancellation callback must not skip the joins.
            (failures ??= []).Add(failure);
        }

        foreach (var thread in threads)
        {
            try
            {
                thread.Join();
            }
            catch (Exception failure) when (failure is not OutOfMemoryException)
            {
                (failures ??= []).Add(failure);
            }
        }

        // A worker may hold work of its own (a checkpoint left running on its lane); it ends
        // before the storages close. The type tests are a bridge: the model engines that still
        // compile the shared pump dispose the same workers by them, so the worker base gains its
        // disposal hook in the P4 PR that deletes the shared pump (concrete-types plan, row 7).
        for (int index = workers.Length - 1; index >= 0; index--)
        {
            try
            {
                if (workers[index] is IAsyncDisposable asynchronous)
                {
                    await asynchronous.DisposeAsync().ConfigureAwait(false);
                }
                else if (workers[index] is IDisposable disposable)
                {
                    disposable.Dispose();
                }
            }
            catch (Exception failure) when (failure is not OutOfMemoryException)
            {
                (failures ??= []).Add(failure);
            }
        }

        try
        {
            await DisposeAsyncCore().ConfigureAwait(false);
        }
        catch (Exception failure) when (failure is not OutOfMemoryException)
        {
            (failures ??= []).Add(failure);
        }

        _stop.Dispose();
        if (failures is not null)
        {
            throw new AggregateException($"One or more components of engine '{_name}' failed to close.", failures);
        }
    }

    /// <summary>
    /// Attaches a background worker to the engine and starts its pump on a dedicated background
    /// thread. The engine owns the worker from then on: it stops the pump and disposes the worker
    /// (when it is disposable) when the engine is disposed.
    /// </summary>
    /// <param name="worker">The worker.</param>
    /// <exception cref="ArgumentNullException"><paramref name="worker"/> is null.</exception>
    /// <exception cref="ObjectDisposedException">The engine has been disposed.</exception>
    /// <exception cref="InvalidOperationException">
    /// Composition was completed (<see cref="CompleteComposition"/>), the worker is already attached,
    /// or another attached worker has the same name.
    /// </exception>
    protected void AttachWorker(DatabaseEngineWorker worker)
    {
        ArgumentNullException.ThrowIfNull(worker);
        lock (_sync)
        {
            ThrowIfDisposed();
            ThrowIfComposed();
            foreach (var existing in _workers)
            {
                if (ReferenceEquals(existing, worker))
                {
                    throw new InvalidOperationException("A composition product cannot be registered twice.");
                }

                if (string.Equals(existing.Name, worker.Name, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException($"Worker name '{worker.Name}' is already registered.");
                }
            }

            var thread = new Thread(() => Pump(worker))
            {
                IsBackground = true,
                Name = worker.Name,
            };

            _workers.Add(worker);
            _threads.Add(thread);
            try
            {
                thread.Start();
            }
            catch (Exception failure) when (failure is not OutOfMemoryException)
            {
                _workers.Remove(worker);
                _threads.Remove(thread);
                throw;
            }

            Volatile.Write(ref _workerView, Array.AsReadOnly(_workers.ToArray()));
        }
    }

    /// <summary>
    /// Attaches a server composed beneath the engine. The engine owns the server from then on and
    /// disposes it first when the engine is disposed; starting and stopping it is the application's.
    /// </summary>
    /// <param name="server">The server, which must front this engine.</param>
    /// <exception cref="ArgumentNullException"><paramref name="server"/> is null.</exception>
    /// <exception cref="ObjectDisposedException">The engine has been disposed.</exception>
    /// <exception cref="InvalidOperationException">
    /// Composition was completed (<see cref="CompleteComposition"/>), the server fronts another
    /// engine, or it is already attached.
    /// </exception>
    protected void AttachServer(DatabaseServer server)
    {
        ArgumentNullException.ThrowIfNull(server);
        lock (_sync)
        {
            ThrowIfDisposed();
            ThrowIfComposed();
            if (!ReferenceEquals(server.Engine, this))
            {
                throw new InvalidOperationException("A nested server must front its owning engine.");
            }

            foreach (var existing in _servers)
            {
                if (ReferenceEquals(existing, server))
                {
                    throw new InvalidOperationException("A composition product cannot be registered twice.");
                }
            }

            _servers.Add(server);
            Volatile.Write(ref _serverView, Array.AsReadOnly(_servers.ToArray()));
        }
    }

    /// <summary>
    /// Freezes the engine's composition: every later <see cref="AttachWorker"/> or
    /// <see cref="AttachServer"/> throws <see cref="InvalidOperationException"/>. Idempotent.
    /// </summary>
    protected void CompleteComposition()
    {
        lock (_sync)
        {
            _composed = true;
        }
    }

    /// <summary>
    /// Throws <see cref="ObjectDisposedException"/> once disposal of the engine has started.
    /// </summary>
    /// <exception cref="ObjectDisposedException">The engine has been disposed.</exception>
    protected void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

    /// <summary>
    /// Creates a database whose name and engine state <see cref="CreateDatabaseAsync"/> checked.
    /// </summary>
    /// <param name="name">The name of the database; never empty.</param>
    /// <param name="cancellationToken">Not canceled when the call starts.</param>
    /// <returns>The newly created database.</returns>
    protected abstract ValueTask<DatabaseInstance> CreateDatabaseCoreAsync(DatabaseName name, CancellationToken cancellationToken);

    /// <summary>
    /// Opens, or reopens when it is offline, a database whose name and engine state
    /// <see cref="OpenDatabaseAsync"/> checked.
    /// </summary>
    /// <param name="name">The name of the database; never empty.</param>
    /// <param name="cancellationToken">Not canceled when the call starts.</param>
    /// <returns>The opened database.</returns>
    protected abstract ValueTask<DatabaseInstance> OpenDatabaseCoreAsync(DatabaseName name, CancellationToken cancellationToken);

    /// <summary>
    /// Drops a database whose name and engine state <see cref="DropDatabaseAsync"/> checked.
    /// </summary>
    /// <param name="name">The name of the database; never empty.</param>
    /// <param name="cancellationToken">Not canceled when the call starts.</param>
    /// <returns>A task that completes once the database is dropped.</returns>
    protected abstract ValueTask DropDatabaseCoreAsync(DatabaseName name, CancellationToken cancellationToken);

    /// <summary>
    /// Enumerates the engine's databases; <see cref="GetDatabasesAsync"/> checked the engine state
    /// and the token when the call was made.
    /// </summary>
    /// <param name="cancellationToken">Cancels the enumeration; not canceled when the call starts.</param>
    /// <returns>An async sequence of the databases.</returns>
    protected abstract IAsyncEnumerable<DatabaseInstance> GetDatabasesCore(CancellationToken cancellationToken);

    /// <summary>
    /// Looks up an open database whose name and engine state <see cref="TryGetDatabase"/> checked.
    /// </summary>
    /// <param name="name">The name of the database; never empty.</param>
    /// <param name="database">When this method returns true, the database.</param>
    /// <returns>True when the database is open in the engine; otherwise false.</returns>
    protected abstract bool TryGetDatabaseCore(DatabaseName name, [MaybeNullWhen(false)] out DatabaseInstance database);

    /// <summary>
    /// Closes the leaf's databases and releases what the leaf owns, once the servers, the worker
    /// pumps and the workers are gone. Durably flushes every open database according to its
    /// storage's durability policy. Called once.
    /// </summary>
    /// <returns>A task that completes once the leaf's part of disposal is done.</returns>
    protected abstract ValueTask DisposeAsyncCore();

    // The worker pump frame: runs the worker until the engine stops it, and never lets the thread
    // end before that. DatabaseEngineWorker.Run returns only on cancellation and records every
    // failure itself; should it ever throw or return early, the run is recorded (the engine reports
    // Faulted until it is disposed), the pump sleeps the backoff and runs it again. Only an
    // OutOfMemoryException escapes the thread, which ends the process.
    private void Pump(DatabaseEngineWorker worker)
    {
        var cancellationToken = _stop.Token;
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                worker.Run(cancellationToken);
                if (cancellationToken.IsCancellationRequested)
                {
                    return;
                }

                Volatile.Write(ref _workerRunFault, new InvalidOperationException(
                    $"Worker '{worker.Name}' returned from Run before its engine stopped it; the engine runs it again."));
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                Volatile.Write(ref _workerRunFault, exception);
            }

            cancellationToken.WaitHandle.WaitOne(DatabaseEngineWorker.FailureBackoff);
        }
    }

    private void ThrowIfComposed()
    {
        if (_composed)
        {
            throw new InvalidOperationException("Engine composition is frozen; workers and servers attach only before it completes.");
        }
    }

    private static void ThrowIfEmpty(DatabaseName name)
    {
        if (name.IsEmpty)
        {
            throw new ArgumentException("A database name is required.", nameof(name));
        }
    }

    IReadOnlyList<IDatabaseEngineWorker> IDatabaseEngine.Workers => Workers;

    IReadOnlyList<IDatabaseServer> IDatabaseEngine.Servers => Servers;

    async ValueTask<IDatabase> IDatabaseEngine.CreateDatabaseAsync(DatabaseName name, CancellationToken cancellationToken)
        => await CreateDatabaseAsync(name, cancellationToken).ConfigureAwait(false);

    async ValueTask<IDatabase> IDatabaseEngine.OpenDatabaseAsync(DatabaseName name, CancellationToken cancellationToken)
        => await OpenDatabaseAsync(name, cancellationToken).ConfigureAwait(false);

    IAsyncEnumerable<IDatabase> IDatabaseEngine.GetDatabasesAsync(CancellationToken cancellationToken)
        => GetDatabasesAsync(cancellationToken);

    bool IDatabaseEngine.TryGetDatabase(DatabaseName name, out IDatabase database)
    {
        bool found = TryGetDatabase(name, out var instance);
        database = instance!;
        return found;
    }
}
