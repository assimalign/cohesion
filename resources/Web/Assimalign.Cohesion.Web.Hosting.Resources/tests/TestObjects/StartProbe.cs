using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.DependencyInjection;
using Assimalign.Cohesion.Hosting;
using Assimalign.Cohesion.Hosting.Resources;

namespace Assimalign.Cohesion.Web.Hosting.Resources.Tests;

/// <summary>
/// An application service that records the CORS policy in effect when its host starts it, as a
/// background service that reads the resource's state would see it.
/// </summary>
internal sealed class StartProbe : IHostService
{
    private readonly WebApplicationContext _context;

    public StartProbe(WebApplicationContext context)
    {
        _context = context;
    }

    public ServiceId Id { get; } = ServiceId.New();

    public WebCorsPolicy? PolicyAtStart { get; private set; }

    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        IResourceService service = _context.ServiceProvider
            .GetRequiredService<IEnumerable<IHostService>>()
            .OfType<IResourceService>()
            .Single();
        PolicyAtStart = ((WebResource)service.ControlPlane.Resource).CorsPolicy;
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken = default)
    {
        return Task.CompletedTask;
    }
}
