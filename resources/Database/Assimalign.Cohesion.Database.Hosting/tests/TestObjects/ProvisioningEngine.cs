using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Database.Hosting.Tests;

internal sealed class ProvisioningEngine : IDatabaseEngine
{
    private readonly List<string> _log;
    private readonly DatabaseException? _openException;
    private bool _databaseIsOpen;
    private bool _databaseExists;

    internal ProvisioningEngine(
        List<string> log,
        bool databaseExists = false,
        DatabaseException? openException = null)
    {
        _log = log;
        _databaseExists = databaseExists;
        _openException = openException;
    }

    public string Name => "provisioning-engine";

    public EngineModel Model => EngineModel.Sql;

    public EngineState State { get; private set; } = EngineState.Running;

    public IReadOnlyList<IDatabaseEngineWorker> Workers => Array.Empty<IDatabaseEngineWorker>();

    public ValueTask<IDatabase> CreateDatabaseAsync(
        string name,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _log.Add("engine:create");
        _databaseExists = true;
        _databaseIsOpen = true;
        return ValueTask.FromResult<IDatabase>(null!);
    }

    public ValueTask<IDatabase> OpenDatabaseAsync(
        string name,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _log.Add("engine:open");

        if (_openException is not null)
        {
            throw _openException;
        }

        if (!_databaseExists)
        {
            throw new DatabaseNotFoundException($"Database '{name}' does not exist.");
        }

        _databaseIsOpen = true;
        return ValueTask.FromResult<IDatabase>(null!);
    }

    public ValueTask DropDatabaseAsync(
        string name,
        CancellationToken cancellationToken = default)
    {
        throw new NotSupportedException();
    }

    public IAsyncEnumerable<IDatabase> GetDatabasesAsync(
        CancellationToken cancellationToken = default)
    {
        throw new NotSupportedException();
    }

    public bool TryGetDatabase(string name, out IDatabase database)
    {
        database = null!;
        return _databaseIsOpen;
    }

    public void Dispose()
    {
        State = EngineState.Disposed;
    }

    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }
}
