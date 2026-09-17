using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Database.Hosting.Tests;

internal sealed class ProvisioningEngine : IDatabaseEngine
{
    private readonly List<string> _log;
    private readonly DatabaseException? _openException;
    private ProvisioningDatabase? _database;
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
        _database = new ProvisioningDatabase(name, this, _log);
        return ValueTask.FromResult<IDatabase>(_database);
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
        _database ??= new ProvisioningDatabase(name, this, _log);
        return ValueTask.FromResult<IDatabase>(_database);
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
        database = _database!;
        return _databaseIsOpen && _database is not null;
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

internal sealed class ProvisioningDatabase(
    string name,
    IDatabaseEngine engine,
    List<string> log) : IDatabase, IDatabaseSchemaProvisioner
{
    public DatabaseName Name { get; } = name;

    public IDatabaseEngine Engine { get; } = engine;

    public CompiledSchema? AppliedSchema { get; private set; }

    public ValueTask<SchemaMigrationResult> ApplySchemaAsync(
        CompiledSchema schema,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        log.Add("engine:apply");
        AppliedSchema = schema;
        return ValueTask.FromResult(new SchemaMigrationResult(null, schema.Hash, 1, false));
    }

    public ValueTask<IDatabaseSession> CreateSessionAsync(CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

    public void Dispose()
    {
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
