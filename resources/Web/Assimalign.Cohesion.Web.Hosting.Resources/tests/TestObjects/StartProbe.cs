using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Hosting;
using Assimalign.Cohesion.Web.Cors;
using Assimalign.Cohesion.Web.Hosting.Resources.Internal;

namespace Assimalign.Cohesion.Web.Hosting.Resources.Tests;

/// <summary>
/// An application service that records the CORS policy in effect when its host starts it, as a
/// background service that reads the application's settings would see it.
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
        IWebApplicationSetting<CorsPolicy?> cors = _context.Settings.OfType<IWebApplicationSetting<CorsPolicy?>>().Single();
        PolicyAtStart = WebCorsPolicyMapping.FromPolicy(cors.Value);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken = default)
    {
        return Task.CompletedTask;
    }
}
