using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Hosting;

namespace Assimalign.Cohesion.Database.Hosting.Internal;

/// <summary>
/// The endpoint host service: maps a <see cref="DatabaseServer"/>'s bind and drain
/// operations directly onto the host lifecycle.
/// </summary>
/// <remarks>
/// The server owns its own two-phase drain; this service only maps that lifecycle
/// onto the hosting execution menu. <see cref="DatabaseApplication"/> constructs
/// one per server of the application's engines (<see cref="DatabaseApplicationContext.Servers"/>)
/// and registers them last, so every endpoint starts after — and drains before —
/// every other composed service.
/// </remarks>
[global::System.Diagnostics.DebuggerDisplay("DatabaseServerHostService")]
[global::System.Diagnostics.DebuggerTypeProxy(typeof(global::Assimalign.Cohesion.Database.Hosting.Internal.DatabaseServerHostService.DebugView))]
internal sealed class DatabaseServerHostService : IHostService
{
    private readonly DatabaseServer _server;

    internal DatabaseServerHostService(DatabaseServer server)
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

    private sealed class DebugView
    {
        private readonly global::Assimalign.Cohesion.Database.Hosting.Internal.DatabaseServerHostService _target;

        public DebugView(global::Assimalign.Cohesion.Database.Hosting.Internal.DatabaseServerHostService value)
        {
            _target = value;
        }

        public global::Assimalign.Cohesion.Hosting.ServiceId Id => _target.Id;
    }
}
