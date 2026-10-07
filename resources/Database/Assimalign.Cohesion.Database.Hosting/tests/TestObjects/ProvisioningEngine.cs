using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Database.Storage;

namespace Assimalign.Cohesion.Database.Hosting.Tests;

/// <summary>
/// An engine of the root base with one database, which the provisioning tests open, create and
/// apply a schema to, recording each call into a shared log. It implements only the protected
/// cores; the base makes the name, disposal and cancellation checks.
/// </summary>
internal sealed class ProvisioningEngine : DatabaseEngine
{
    private readonly List<string> _log;
    private readonly DatabaseException? _openException;
    private readonly bool _supportsSchemaProvisioning;
    private ProvisioningDatabase? _database;
    private bool _databaseIsOpen;
    private bool _databaseExists;

    internal ProvisioningEngine(
        List<string> log,
        bool databaseExists = false,
        DatabaseException? openException = null,
        bool supportsSchemaProvisioning = true)
        : base("provisioning-engine", EngineModel.Sql)
    {
        _log = log;
        _databaseExists = databaseExists;
        _openException = openException;
        _supportsSchemaProvisioning = supportsSchemaProvisioning;
    }

    /// <inheritdoc />
    public override IReadOnlyList<DatabaseName> OfflineDatabases => [];

    protected override ValueTask<DatabaseInstance> CreateDatabaseCoreAsync(DatabaseName name, CancellationToken cancellationToken)
    {
        _log.Add("engine:create");
        _databaseExists = true;
        _databaseIsOpen = true;
        _database = new ProvisioningDatabase(name, this, _log, _supportsSchemaProvisioning);
        return ValueTask.FromResult<DatabaseInstance>(_database);
    }

    protected override ValueTask<DatabaseInstance> OpenDatabaseCoreAsync(DatabaseName name, CancellationToken cancellationToken)
    {
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
        _database ??= new ProvisioningDatabase(name, this, _log, _supportsSchemaProvisioning);
        return ValueTask.FromResult<DatabaseInstance>(_database);
    }

    protected override ValueTask DropDatabaseCoreAsync(DatabaseName name, CancellationToken cancellationToken)
        => throw new NotSupportedException();

    protected override IAsyncEnumerable<DatabaseInstance> GetDatabasesCore(CancellationToken cancellationToken)
        => throw new NotSupportedException();

    protected override bool TryGetDatabaseCore(DatabaseName name, [MaybeNullWhen(false)] out DatabaseInstance database)
    {
        database = _database;
        return _databaseIsOpen && database is not null;
    }

    protected override void ForgetClosedDatabaseCore(DatabaseInstance database)
    {
        if (ReferenceEquals(database, _database))
        {
            _databaseIsOpen = false;
            _database = null;
        }
    }

    protected override StorageOfflineException? GetOfflineErrorCore(DatabaseName name) => null;

    protected override bool TakeDatabaseOfflineCore(DatabaseName name, StorageOfflineCause cause, string reason, Exception failure)
        => false;

    protected override ValueTask DisposeAsyncCore() => ValueTask.CompletedTask;
}

/// <summary>
/// The provisioning engine's database: it records the schema the hosting provisioner applies,
/// through the root base's capability flag and schema core.
/// </summary>
internal sealed class ProvisioningDatabase : DatabaseInstance
{
    private readonly List<string> _log;

    /// <summary>
    /// Initializes a new instance of the <see cref="ProvisioningDatabase"/> class.
    /// </summary>
    /// <param name="name">The name of the database.</param>
    /// <param name="engine">The engine that owns the database.</param>
    /// <param name="log">The shared call log that records schema application.</param>
    /// <param name="supportsSchemaProvisioning">Whether the database provisions schemas.</param>
    public ProvisioningDatabase(
        DatabaseName name,
        DatabaseEngine engine,
        List<string> log,
        bool supportsSchemaProvisioning)
        : base(name, engine, supportsSchemaProvisioning)
    {
        _log = log;
    }

    public CompiledSchema? AppliedSchema { get; private set; }

    protected override ValueTask<SchemaMigrationResult> ApplySchemaCoreAsync(
        CompiledSchema schema,
        CancellationToken cancellationToken)
    {
        _log.Add("engine:apply");
        AppliedSchema = schema;
        return ValueTask.FromResult(new SchemaMigrationResult(null, schema.Hash, 1, false));
    }

    protected override ValueTask<DatabaseSession> CreateSessionCoreAsync(CancellationToken cancellationToken)
        => throw new NotSupportedException();

    protected override void DisposeCore()
    {
    }

    protected override ValueTask DisposeAsyncCore() => ValueTask.CompletedTask;
}
