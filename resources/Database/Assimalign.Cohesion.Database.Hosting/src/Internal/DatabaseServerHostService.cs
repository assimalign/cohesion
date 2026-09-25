using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Hosting;

namespace Assimalign.Cohesion.Database.Hosting.Internal;

/// <summary>
/// The endpoint host service: maps a <see cref="IDatabaseServer"/>'s bind and drain
/// operations directly onto the host lifecycle.
/// </summary>
/// <remarks>
/// The server owns its own two-phase drain; this service only maps that lifecycle
/// onto the hosting execution menu. <see cref="DatabaseApplication"/> constructs
/// one per server registered on <see cref="DatabaseApplicationOptions.Servers"/>
/// and registers them last, so every endpoint starts after — and drains before —
/// every other composed service.
/// </remarks>
internal sealed class DatabaseServerHostService : IHostService
{
    private readonly IDatabaseServer _server;

    internal DatabaseServerHostService(IDatabaseServer server)
    {
        _server = server;
        Id = ServiceId.New();
    }

    /// <inheritdoc />
    public ServiceId Id { get; }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken = default)
        => _server.StartAsync(cancellationToken);

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken = default)
        => _server.StopAsync(cancellationToken);
}
