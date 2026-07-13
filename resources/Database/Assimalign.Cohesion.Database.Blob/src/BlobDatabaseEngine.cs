using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Database.Blob;

/// <summary>
/// The blob-model database engine: manages the lifecycle of blob databases.
/// </summary>
/// <remarks>
/// Scaffold: the engine surface is final; lifecycle and storage composition are
/// implemented by the Blob work items (Project #13, L03.02.04 tree).
/// </remarks>
public sealed class BlobDatabaseEngine : IDatabaseEngine
{
    private readonly BlobDatabaseEngineOptions _options;
    private EngineState _state;

    private BlobDatabaseEngine(BlobDatabaseEngineOptions options)
    {
        _options = options;
        Name = options.EngineName ?? "blob-engine";
        _state = EngineState.Idle;
    }

    /// <inheritdoc />
    public string Name { get; }

    /// <inheritdoc />
    public EngineState State => _state;

    /// <inheritdoc />
    public EngineModel Model => EngineModel.Blob;

    /// <summary>
    /// Creates a new blob database engine from options.
    /// </summary>
    /// <param name="options">Engine creation options.</param>
    /// <returns>A new engine instance.</returns>
    public static BlobDatabaseEngine Create(BlobDatabaseEngineOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return new BlobDatabaseEngine(options);
    }

    /// <inheritdoc />
    public IReadOnlyList<IDatabaseEngineWorker> Workers => Array.Empty<IDatabaseEngineWorker>();

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _state = EngineState.Running;
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (_state == EngineState.Running)
        {
            _state = EngineState.Stopped;
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public ValueTask<IDatabase> CreateDatabaseAsync(string name, CancellationToken cancellationToken = default)
        => throw new NotImplementedException("Blob engine lifecycle is implemented by the L03.02.04 work items.");

    /// <inheritdoc />
    public ValueTask<IDatabase> OpenDatabaseAsync(string name, CancellationToken cancellationToken = default)
        => throw new NotImplementedException("Blob engine lifecycle is implemented by the L03.02.04 work items.");

    /// <inheritdoc />
    public ValueTask DropDatabaseAsync(string name, CancellationToken cancellationToken = default)
        => throw new NotImplementedException("Blob engine lifecycle is implemented by the L03.02.04 work items.");

    /// <inheritdoc />
    public IAsyncEnumerable<IDatabase> GetDatabasesAsync(CancellationToken cancellationToken = default)
        => throw new NotImplementedException("Blob engine lifecycle is implemented by the L03.02.04 work items.");

    /// <inheritdoc />
    public bool TryGetDatabase(string name, out IDatabase database)
    {
        database = null!;
        return false;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _state = EngineState.Stopped;
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }
}
