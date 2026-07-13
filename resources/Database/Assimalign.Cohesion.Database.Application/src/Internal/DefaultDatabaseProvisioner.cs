using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Hosting;

namespace Assimalign.Cohesion.Database.Application.Internal;

/// <summary>
/// Ensures the host's default database exists and is open before the endpoint
/// starts: opens it when its files exist (a restart), creates it otherwise (first
/// launch). Registered as an additional host service, so it runs after the engine
/// has started and before the wire endpoint accepts — a client can always bind the
/// default database.
/// </summary>
/// <remarks>
/// The wire protocol has no CREATE DATABASE verb (database provisioning is a
/// deployment concern in the MVP), so a fresh server process must bring its
/// database with it or no client could ever bind one. See docs/DESIGN.md for the
/// convention.
/// </remarks>
internal sealed class DefaultDatabaseProvisioner : IHostService
{
    private readonly IDatabaseEngine _engine;
    private readonly string _databaseName;

    internal DefaultDatabaseProvisioner(IDatabaseEngine engine, string databaseName)
    {
        _engine = engine;
        _databaseName = databaseName;
        Id = ServiceId.New();
    }

    /// <inheritdoc />
    public ServiceId Id { get; }

    /// <inheritdoc />
    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (_engine.TryGetDatabase(_databaseName, out _))
        {
            return;
        }

        try
        {
            await _engine.OpenDatabaseAsync(_databaseName, cancellationToken).ConfigureAwait(false);
        }
        catch (DatabaseException)
        {
            // The database does not exist yet: first launch on an empty data path.
            await _engine.CreateDatabaseAsync(_databaseName, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken = default)
        => Task.CompletedTask;
}
