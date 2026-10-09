using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Database.Storage;

namespace Assimalign.Cohesion.Database.Tests;

/// <summary>
/// An engine over an in-memory set of <see cref="TestDatabase"/>s: implements only the base's
/// protected cores, records their calls, and exposes the protected attach and freeze members.
/// </summary>
/// <remarks>
/// A created database exists until it is dropped, like a database's files: the engine tracks the
/// open instance of each, forgets an instance whose close ended (as a model leaf does, through
/// its lock-free snapshot first), and an open of an existing database it does not track creates
/// a new instance, a reopen.
/// </remarks>
internal sealed class TestEngine : DatabaseEngine
{
    private readonly Dictionary<string, TestDatabase> _databases = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _existing = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<GiveUp> _giveUps = [];
    private readonly HashSet<string> _takenOffline = new(StringComparer.OrdinalIgnoreCase);
    private TestDatabase[] _snapshot = [];
    private int _coreCalls;
    private int _forgets;
    private int _reopens;
    private int _takeOfflineCalls;

    public TestEngine(
        string name = "test-engine",
        TestLog? log = null,
        TimeSpan? workerFailureWindow = null,
        int workerFailureMinimumPasses = DefaultWorkerFailureMinimumPasses,
        TimeProvider? time = null)
        : base(name, EngineModel.Sql, workerFailureWindow ?? DefaultWorkerFailureWindow, workerFailureMinimumPasses, time)
    {
        Log = log ?? new TestLog();
    }

    /// <summary>
    /// Gets every call the base made to take a database offline, in order, whether or not the
    /// leaf took it offline.
    /// </summary>
    public IReadOnlyList<GiveUp> GiveUps
    {
        get
        {
            lock (_giveUps)
            {
                return [.. _giveUps];
            }
        }
    }

    /// <summary>Gets the names of the databases the leaf took offline.</summary>
    public IReadOnlyCollection<string> TakenOffline
    {
        get
        {
            lock (_giveUps)
            {
                return [.. _takenOffline];
            }
        }
    }

    /// <summary>Gets the names the offline-error core was asked about, in order.</summary>
    public List<string> OfflineErrorLookups { get; } = [];

    /// <summary>
    /// Gets or sets a gate the take-offline core waits on before it acts, as a storage whose
    /// journal lock a hung fsync holds would make it wait.
    /// </summary>
    public ManualResetEventSlim? TakeOfflineGate { get; set; }

    /// <summary>Gets or sets a failure the take-offline core throws once past its gate.</summary>
    public Exception? TakeOfflineFailure { get; set; }

    /// <summary>Gets how many calls entered the take-offline core.</summary>
    public int TakeOfflineCalls => Volatile.Read(ref _takeOfflineCalls);

    /// <summary>Gets the threads the take-offline core ran on.</summary>
    public List<int> TakeOfflineThreads { get; } = [];

    public TestLog Log { get; }

    /// <summary>Gets how many times a database core ran.</summary>
    public int CoreCalls => Volatile.Read(ref _coreCalls);

    /// <summary>Gets how many closed databases the base handed to the leaf.</summary>
    public int Forgets => Volatile.Read(ref _forgets);

    /// <summary>Gets how many existing databases the open core opened again.</summary>
    public int Reopens => Volatile.Read(ref _reopens);

    /// <summary>Gets or sets whether the leaf keeps tracking a closed database: a leaf defect.</summary>
    public bool KeepClosedDatabases { get; set; }

    /// <summary>Gets or sets the offline list the leaf computes.</summary>
    public IReadOnlyList<DatabaseName> Offline { get; set; } = [];

    /// <summary>Gets or sets a failure the disposal core throws after it closed the databases.</summary>
    public Exception? DisposeFailure { get; set; }

    /// <summary>Gets or sets the gate every database the engine creates or opens holds its close on.</summary>
    public TaskCompletionSource? CloseGate { get; set; }

    /// <summary>
    /// Gets or sets a task the open core awaits before it looks the database up, so a test can hold
    /// several opens in flight together.
    /// </summary>
    public Task? OpenBarrier { get; set; }

    public override IReadOnlyList<DatabaseName> OfflineDatabases => Offline;

    public bool Disposed => IsDisposed;

    public void Attach(DatabaseEngineWorker worker) => AttachWorker(worker);

    public void Attach(DatabaseServer server) => AttachServer(server);

    public void Complete() => CompleteComposition();

    /// <summary>
    /// Gets whether the calling thread holds the lock the database cores run under, which they
    /// throw inside: the event source's tests read it while an event is written.
    /// </summary>
    public bool HoldsRegistryLock => Monitor.IsEntered(_databases);

    /// <summary>Gets whether the leaf still tracks this instance.</summary>
    public bool Tracks(DatabaseInstance database)
    {
        lock (_databases)
        {
            return _databases.TryGetValue(database.Name, out var tracked) && ReferenceEquals(tracked, database);
        }
    }

    /// <summary>The base's release of a worker no engine owns, as a model's builder reaches it.</summary>
    public static ValueTask Release(DatabaseEngineWorker worker) => ReleaseUnownedWorkerAsync(worker);

    protected override ValueTask<DatabaseInstance> CreateDatabaseCoreAsync(DatabaseName name, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _coreCalls);
        lock (_databases)
        {
            if (_databases.ContainsKey(name) || _existing.Contains(name))
            {
                throw new DatabaseException($"Database '{name}' already exists.");
            }

            var database = new TestDatabase(name, this, log: Log, closeGate: CloseGate);
            _databases.Add(name, database);
            _existing.Add(name);
            Publish();
            return new ValueTask<DatabaseInstance>(database);
        }
    }

    protected override ValueTask<DatabaseInstance> OpenDatabaseCoreAsync(DatabaseName name, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _coreCalls);
        if (OpenBarrier is { } barrier)
        {
            return OpenAfterAsync(barrier, name);
        }

        return new ValueTask<DatabaseInstance>(OpenTracked(name));
    }

    private async ValueTask<DatabaseInstance> OpenAfterAsync(Task barrier, DatabaseName name)
    {
        await barrier.ConfigureAwait(false);
        return OpenTracked(name);
    }

    private DatabaseInstance OpenTracked(DatabaseName name)
    {
        lock (_databases)
        {
            // A tracked database is returned as it is, a closing one too: the base waits for its
            // close, which forgets it, and calls the core again.
            if (_databases.TryGetValue(name, out var database))
            {
                return database;
            }

            if (!_existing.Contains(name))
            {
                throw new DatabaseNotFoundException($"Database '{name}' does not exist.");
            }

            var reopened = new TestDatabase(name, this, log: Log, closeGate: CloseGate);
            _databases.Add(name, reopened);
            Interlocked.Increment(ref _reopens);
            Publish();
            return reopened;
        }
    }

    protected override ValueTask DropDatabaseCoreAsync(DatabaseName name, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _coreCalls);
        lock (_databases)
        {
            if (!_existing.Remove(name))
            {
                throw new DatabaseNotFoundException($"Database '{name}' does not exist.");
            }

            // Let go of the instance first, then close it under the lock: the close waits for one a
            // holder started, whose forget finds the instance gone from the snapshot.
            if (_databases.Remove(name, out var database))
            {
                Publish();
                database.Dispose();
            }
        }

        return ValueTask.CompletedTask;
    }

    protected override async IAsyncEnumerable<DatabaseInstance> GetDatabasesCore([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _coreCalls);
        TestDatabase[] snapshot;
        lock (_databases)
        {
            snapshot = [.. _databases.Values.OrderBy(database => (string)database.Name, StringComparer.OrdinalIgnoreCase)];
        }

        foreach (var database in snapshot)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Yield();
            yield return database;
        }
    }

    protected override bool TryGetDatabaseCore(DatabaseName name, [MaybeNullWhen(false)] out DatabaseInstance database)
    {
        Interlocked.Increment(ref _coreCalls);
        lock (_databases)
        {
            bool found = _databases.TryGetValue(name, out var instance);
            database = instance;
            return found;
        }
    }

    protected override StorageOfflineException? GetOfflineErrorCore(DatabaseName name)
    {
        Interlocked.Increment(ref _coreCalls);
        lock (OfflineErrorLookups)
        {
            OfflineErrorLookups.Add(name);
        }

        // The double has no storage whose offline error it could report.
        return null;
    }

    /// <summary>
    /// A model leaf's shape: takes a tracked, open database offline once, and refuses one it does
    /// not track, one closing, and one already offline.
    /// </summary>
    protected override bool TakeDatabaseOfflineCore(DatabaseName name, StorageOfflineCause cause, string reason, Exception failure)
    {
        Interlocked.Increment(ref _takeOfflineCalls);
        lock (TakeOfflineThreads)
        {
            TakeOfflineThreads.Add(Environment.CurrentManagedThreadId);
        }

        TakeOfflineGate?.Wait();
        if (TakeOfflineFailure is { } thrown)
        {
            throw thrown;
        }

        bool taken;
        TestDatabase[] snapshot = Volatile.Read(ref _snapshot);
        lock (_giveUps)
        {
            taken = Array.Exists(snapshot, database => string.Equals(database.Name, name, StringComparison.OrdinalIgnoreCase) && !database.IsClosing)
                && _takenOffline.Add(name);
            _giveUps.Add(new GiveUp(name, cause, reason, failure, taken));
        }

        return taken;
    }

    protected override void ForgetClosedDatabaseCore(DatabaseInstance database)
    {
        Interlocked.Increment(ref _forgets);
        if (KeepClosedDatabases)
        {
            return;
        }

        // A model leaf's shape: the lock-free snapshot first, then the lock, never waited on
        // indefinitely, because the leaf may hold it while it waits for this very close.
        while (Array.IndexOf(Volatile.Read(ref _snapshot), database) >= 0)
        {
            if (!Monitor.TryEnter(_databases, TimeSpan.FromMilliseconds(10)))
            {
                continue;
            }

            try
            {
                if (_databases.TryGetValue(database.Name, out var tracked) && ReferenceEquals(tracked, database))
                {
                    _databases.Remove(database.Name);
                    Publish();
                }

                return;
            }
            finally
            {
                Monitor.Exit(_databases);
            }
        }
    }

    protected override async ValueTask DisposeAsyncCore()
    {
        Log.Add("engine:databases");
        TestDatabase[] databases;
        lock (_databases)
        {
            databases = [.. _databases.Values];
            _databases.Clear();
            Publish();
        }

        foreach (var database in databases)
        {
            await database.DisposeAsync().ConfigureAwait(false);
        }

        if (DisposeFailure is { } failure)
        {
            throw failure;
        }
    }

    // Under the lock: publishes the tracked databases for the lock-free check of the forget.
    private void Publish() => Volatile.Write(ref _snapshot, [.. _databases.Values]);

    /// <summary>A call the base made to take a database offline, and whether the leaf did.</summary>
    /// <param name="Name">The database's name.</param>
    /// <param name="Cause">The cause.</param>
    /// <param name="Reason">The reason, for the storage's message.</param>
    /// <param name="Failure">The worker's last failure.</param>
    /// <param name="Taken">Whether the leaf took the database offline.</param>
    public sealed record GiveUp(string Name, StorageOfflineCause Cause, string Reason, Exception Failure, bool Taken);
}
