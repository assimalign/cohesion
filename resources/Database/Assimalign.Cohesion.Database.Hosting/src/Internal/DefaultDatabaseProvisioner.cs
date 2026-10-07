using System;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Hosting;

namespace Assimalign.Cohesion.Database.Hosting.Internal;

/// <summary>
/// Ensures a declared database is open before any registered server begins accepting.
/// </summary>
internal sealed class DefaultDatabaseProvisioner : IHostService
{
    private readonly DatabaseEngine _engine;
    private readonly CompiledSchema _schema;

    internal DefaultDatabaseProvisioner(DatabaseEngine engine, CompiledSchema schema)
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

        if (!_engine.TryGetDatabase(_schema.Name, out DatabaseInstance? database))
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

        // The capability is a flag on the root base (concrete-types plan, row 8), fixed when the
        // model creates the database: only a model that provisions schemas sets it.
        if (!database.SupportsSchemaProvisioning)
        {
            throw new NotSupportedException(
                $"Database engine '{_engine.Name}' ({_engine.Model}) does not support compiled schema provisioning.");
        }

        await database.ApplySchemaAsync(_schema, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken = default)
    {
        return Task.CompletedTask;
    }
}
