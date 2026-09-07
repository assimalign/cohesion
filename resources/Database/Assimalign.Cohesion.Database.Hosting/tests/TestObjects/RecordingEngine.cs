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

    public void Dispose()
    {
        _state = EngineState.Disposed;
    }

    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }
}
