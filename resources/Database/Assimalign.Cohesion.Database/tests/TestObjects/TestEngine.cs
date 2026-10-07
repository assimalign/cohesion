using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

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
    private TestDatabase[] _snapshot = [];
    private int _coreCalls;
    private int _forgets;
    private int _reopens;

    public TestEngine(string name = "test-engine", TestLog? log = null)
        : base(name, EngineModel.Sql)
    {
        Log = log ?? new TestLog();
    }

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

    public override IReadOnlyList<DatabaseName> OfflineDatabases => Offline;

    public bool Disposed => IsDisposed;

    public void Attach(DatabaseEngineWorker worker) => AttachWorker(worker);

    public void Attach(DatabaseServer server) => AttachServer(server);

    public void Complete() => CompleteComposition();

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
        lock (_databases)
        {
            // A tracked database is returned as it is, a closing one too: the base waits for its
            // close, which forgets it, and calls the core again.
            if (_databases.TryGetValue(name, out var database))
            {
                return new ValueTask<DatabaseInstance>(database);
            }

            if (!_existing.Contains(name))
            {
                throw new DatabaseNotFoundException($"Database '{name}' does not exist.");
            }

            var reopened = new TestDatabase(name, this, log: Log, closeGate: CloseGate);
            _databases.Add(name, reopened);
            Interlocked.Increment(ref _reopens);
            Publish();
            return new ValueTask<DatabaseInstance>(reopened);
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
}
