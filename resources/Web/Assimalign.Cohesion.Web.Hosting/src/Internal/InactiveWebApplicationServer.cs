using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Web.Hosting.Internal;

/// <summary>
/// Occupies the default server's registration when no default listener was configured, so a
/// custom-only composition neither starts an empty listener nor loses the default's lifecycle
/// position. <see cref="WebApplicationContext"/> excludes it from the servers and the host
/// lifecycle.
/// </summary>
internal sealed class InactiveWebApplicationServer : IWebApplicationServer
{
    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken = default)
    {
        return Task.CompletedTask;
    }
}
