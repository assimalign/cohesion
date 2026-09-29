using System;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Hosting;

namespace Assimalign.Cohesion.Web.Hosting.Internal;

/// <summary>
/// Adapts the Web root's server lifecycle contract to the host lifecycle without requiring
/// the contracts-only Web assembly to reference Hosting.
/// </summary>
/// <remarks>
/// <see cref="WebApplicationContext"/> creates one adapter per registered server that is not
/// itself an <see cref="IHostService"/>, once, when it snapshots the server phase.
/// </remarks>
internal sealed class WebApplicationServerLifecycleAdapter : IHostService
{
    private readonly IWebApplicationServer _server;

    internal WebApplicationServerLifecycleAdapter(IWebApplicationServer server)
    {
        _server = server ?? throw new ArgumentNullException(nameof(server));
    }

    public ServiceId Id { get; } = ServiceId.New();

    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        return _server.StartAsync(cancellationToken);
    }

    public Task StopAsync(CancellationToken cancellationToken = default)
    {
        return _server.StopAsync(cancellationToken);
    }
}
