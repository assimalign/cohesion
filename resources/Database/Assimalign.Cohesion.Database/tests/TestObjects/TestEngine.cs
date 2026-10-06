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
internal sealed class TestEngine : DatabaseEngine
{
    private readonly Dictionary<string, TestDatabase> _databases = new(StringComparer.OrdinalIgnoreCase);
    private int _coreCalls;

    public TestEngine(string name = "test-engine", TestLog? log = null)
        : base(name, EngineModel.Sql)
    {
        Log = log ?? new TestLog();
    }

    public TestLog Log { get; }

    /// <summary>Gets how many times a database core ran.</summary>
    public int CoreCalls => Volatile.Read(ref _coreCalls);

    /// <summary>Gets or sets the offline list the leaf computes.</summary>
    public IReadOnlyList<DatabaseName> Offline { get; set; } = [];

    /// <summary>Gets or sets a failure the disposal core throws after it closed the databases.</summary>
    public Exception? DisposeFailure { get; set; }

    public override IReadOnlyList<DatabaseName> OfflineDatabases => Offline;

    public bool Disposed => IsDisposed;

    public void Attach(DatabaseEngineWorker worker) => AttachWorker(worker);

    public void Attach(DatabaseServer server) => AttachServer(server);

    public void Complete() => CompleteComposition();

    /// <summary>The base's release of a worker no engine owns, as a model's builder reaches it.</summary>
    public static ValueTask Release(DatabaseEngineWorker worker) => ReleaseUnownedWorkerAsync(worker);

    protected override ValueTask<DatabaseInstance> CreateDatabaseCoreAsync(DatabaseName name, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _coreCalls);
        lock (_databases)
        {
            if (_databases.ContainsKey(name))
            {
                throw new DatabaseException($"Database '{name}' already exists.");
            }

            var database = new TestDatabase(name, this, log: Log);
            _databases.Add(name, database);
            return new ValueTask<DatabaseInstance>(database);
        }
    }

    protected override ValueTask<DatabaseInstance> OpenDatabaseCoreAsync(DatabaseName name, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _coreCalls);
        lock (_databases)
        {
            return _databases.TryGetValue(name, out var database)
                ? new ValueTask<DatabaseInstance>(database)
                : throw new DatabaseNotFoundException($"Database '{name}' does not exist.");
        }
    }

    protected override ValueTask DropDatabaseCoreAsync(DatabaseName name, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _coreCalls);
        TestDatabase? database;
        lock (_databases)
        {
            if (!_databases.Remove(name, out database))
            {
                throw new DatabaseNotFoundException($"Database '{name}' does not exist.");
            }
        }

        database.Dispose();
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

    protected override async ValueTask DisposeAsyncCore()
    {
        Log.Add("engine:databases");
        TestDatabase[] databases;
        lock (_databases)
        {
            databases = [.. _databases.Values];
            _databases.Clear();
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
}
