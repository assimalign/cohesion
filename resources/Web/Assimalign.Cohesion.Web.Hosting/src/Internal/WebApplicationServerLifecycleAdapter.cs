using System;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Hosting;

namespace Assimalign.Cohesion.Web.Hosting.Internal;

/// <summary>
/// Adapts the Web root's server lifecycle contract to the host lifecycle without requiring
/// the contracts-only Web assembly to reference Hosting.
/// </summary>
internal sealed class WebApplicationServerLifecycleAdapter : IHostService
{
    internal WebApplicationServerLifecycleAdapter(IWebApplicationServer server)
    {
        Server = server ?? throw new ArgumentNullException(nameof(server));
    }

    public ServiceId Id { get; } = ServiceId.New();

    internal IWebApplicationServer Server { get; }

    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        return Server.StartAsync(cancellationToken);
    }

    public Task StopAsync(CancellationToken cancellationToken = default)
    {
        return Server.StopAsync(cancellationToken);
    }
}
