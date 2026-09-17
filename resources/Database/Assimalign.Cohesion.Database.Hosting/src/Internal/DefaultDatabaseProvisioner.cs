using System;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Hosting;

namespace Assimalign.Cohesion.Database.Hosting;

/// <summary>
/// Ensures a declared database is open before any registered server begins accepting.
/// </summary>
internal sealed class DefaultDatabaseProvisioner : IHostService
{
    private readonly IDatabaseEngine _engine;
    private readonly CompiledSchema _schema;

    internal DefaultDatabaseProvisioner(IDatabaseEngine engine, CompiledSchema schema)
    {
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(schema);

        _engine = engine;
        _schema = schema;
        Id = ServiceId.New();
    }

    /// <inheritdoc />
    public ServiceId Id { get; }

    /// <inheritdoc />
    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        IDatabase database;
        if (!_engine.TryGetDatabase(_schema.Name, out database!))
        {
            try
            {
                database = await _engine.OpenDatabaseAsync(_schema.Name, cancellationToken).ConfigureAwait(false);
            }
            catch (DatabaseNotFoundException)
            {
                // Opening a database that has not been materialized is the first-launch path.
                database = await _engine.CreateDatabaseAsync(_schema.Name, cancellationToken).ConfigureAwait(false);
            }
        }

        if (database is not IDatabaseSchemaProvisioner provisioner)
        {
            throw new NotSupportedException(
                $"Database engine '{_engine.Name}' ({_engine.Model}) does not support compiled schema provisioning.");
        }

        await provisioner.ApplySchemaAsync(_schema, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken = default)
    {
        return Task.CompletedTask;
    }
}
