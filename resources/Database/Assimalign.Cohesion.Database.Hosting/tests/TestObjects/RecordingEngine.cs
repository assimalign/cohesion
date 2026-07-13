using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Database.Hosting.Tests;

/// <summary>
/// A minimal <see cref="IDatabaseEngine"/> that records its lifecycle calls into a
/// shared log so tests can assert start/stop ordering relative to other services.
/// </summary>
internal sealed class RecordingEngine : IDatabaseEngine
{
    private readonly List<string> _log;
    private readonly List<IDatabaseEngineWorker> _workers = new();
    private EngineState _state = EngineState.Idle;

    public RecordingEngine(List<string> log)
    {
        _log = log;
    }

    public string Name => "recording-engine";

    public EngineState State => _state;

    public EngineModel Model => EngineModel.Sql;

    public IReadOnlyList<IDatabaseEngineWorker> Workers => _workers;

    /// <summary>
    /// Adds a worker to the engine's inventory (before composing the application).
    /// </summary>
    public void AddWorker(IDatabaseEngineWorker worker) => _workers.Add(worker);

    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        _log.Add("engine:start");
        _state = EngineState.Running;
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken = default)
    {
        _log.Add("engine:stop");
        _state = EngineState.Stopped;
        return Task.CompletedTask;
    }

    public ValueTask<IDatabase> CreateDatabaseAsync(string name, CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

    public ValueTask<IDatabase> OpenDatabaseAsync(string name, CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

    public ValueTask DropDatabaseAsync(string name, CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

    public IAsyncEnumerable<IDatabase> GetDatabasesAsync(CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

    public bool TryGetDatabase(string name, out IDatabase database)
    {
        database = null!;
        return false;
    }

    public void Dispose() { }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
