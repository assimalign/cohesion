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
    private readonly string _databaseName;

    internal DefaultDatabaseProvisioner(IDatabaseEngine engine, string databaseName)
    {
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentException.ThrowIfNullOrWhiteSpace(databaseName);

        _engine = engine;
        _databaseName = databaseName;
        Id = ServiceId.New();
    }

    /// <inheritdoc />
    public ServiceId Id { get; }

    /// <inheritdoc />
    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (_engine.TryGetDatabase(_databaseName, out _))
        {
            return;
        }

        try
        {
            await _engine.OpenDatabaseAsync(_databaseName, cancellationToken).ConfigureAwait(false);
        }
        catch (DatabaseNotFoundException)
        {
            // Opening a database that has not been materialized is the first-launch path.
            await _engine.CreateDatabaseAsync(_databaseName, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken = default)
    {
        return Task.CompletedTask;
    }
}
