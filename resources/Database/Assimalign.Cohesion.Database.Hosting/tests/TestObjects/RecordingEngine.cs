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
    private bool _disposed;

    public string Name => "recording-engine";

    public EngineState State => _disposed ? EngineState.Disposed : EngineState.Running;

    public EngineModel Model => EngineModel.Sql;

    public IReadOnlyList<IDatabaseEngineWorker> Workers => Array.Empty<IDatabaseEngineWorker>();

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
        _disposed = true;
    }

    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }
}
