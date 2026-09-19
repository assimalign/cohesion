using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Database.Hosting.Tests;

/// <summary>
/// A minimal <see cref="IDatabaseEngine"/> — a data machine with no databases —
/// for composition tests that only need an engine-shaped registration.
/// </summary>
internal sealed class RecordingEngine : IDatabaseEngine
{
    private readonly string _name;
    private readonly IReadOnlyList<IDatabaseEngineWorker> _workers;
    private readonly List<IDatabaseServer> _servers = [];
    private EngineState _state;

    internal RecordingEngine(
        string name = "recording-engine",
        EngineState state = EngineState.Running,
        IReadOnlyList<IDatabaseEngineWorker>? workers = null)
    {
        _name = name;
        _state = state;
        _workers = workers ?? Array.Empty<IDatabaseEngineWorker>();
    }

    public string Name => _name;

    public EngineState State => _state;

    public EngineModel Model => EngineModel.Sql;

    public IReadOnlyList<IDatabaseEngineWorker> Workers => _workers;

    public IReadOnlyList<IDatabaseServer> Servers => _servers;

    internal int DisposeCount { get; private set; }

    internal Exception? DisposeException { get; set; }

    internal void AddServer(Func<IDatabaseEngine, IDatabaseServer> factory) => _servers.Add(factory(this));

    public ValueTask<IDatabase> CreateDatabaseAsync(DatabaseName name, CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

    public ValueTask<IDatabase> OpenDatabaseAsync(DatabaseName name, CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

    public ValueTask DropDatabaseAsync(DatabaseName name, CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

    public IAsyncEnumerable<IDatabase> GetDatabasesAsync(CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

    public bool TryGetDatabase(DatabaseName name, out IDatabase database)
    {
        database = null!;
        return false;
    }

    public void Dispose() => DisposeAsync().GetAwaiter().GetResult();

    public async ValueTask DisposeAsync()
    {
        if (_state == EngineState.Disposed)
        {
            return;
        }
        _state = EngineState.Disposed;
        DisposeCount++;
        for (int index = _servers.Count - 1; index >= 0; index--)
        {
            await _servers[index].DisposeAsync();
        }
        if (DisposeException is not null)
        {
            throw DisposeException;
        }
    }
}
