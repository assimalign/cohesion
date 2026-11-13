using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.ApplicationModel.Hosting;

namespace Assimalign.Cohesion.Database.Hosting.ApplicationModel.Internal;

[System.Diagnostics.DebuggerDisplay("{Name,nq}")]
[System.Diagnostics.DebuggerTypeProxy(typeof(DebugView))]
internal sealed class DatabaseHealthContributor : IApplicationInstanceHealthContributor
{
    private readonly DatabaseApplicationContext _context;

    internal DatabaseHealthContributor(DatabaseApplicationContext context, string name)
    {
        _context = context;
        Name = name;
    }

    public string Name { get; }

    public async ValueTask<ApplicationInstanceHealthContribution> CheckAsync(CancellationToken cancellationToken = default)
    {
        foreach (var observation in await _context.GetHealthAsync(cancellationToken).ConfigureAwait(false))
        {
            if (observation.Name == Name)
            {
                return new ApplicationInstanceHealthContribution(observation.Status switch
                {
                    DatabaseHealthStatus.Healthy => ApplicationInstanceHealthStatus.Healthy,
                    DatabaseHealthStatus.Degraded => ApplicationInstanceHealthStatus.Degraded,
                    _ => ApplicationInstanceHealthStatus.Unhealthy,
                }, observation.Description, observation.Data);
            }
        }

        // A caller may retain a contributor after its database closes. It cannot attest availability.
        return ApplicationInstanceHealthContribution.Unhealthy("The database component is no longer in the application's health inventory.");
    }

    private sealed class DebugView
    {
        private readonly DatabaseHealthContributor _contributor;

        public DebugView(DatabaseHealthContributor contributor) { _contributor = contributor; }

        public string Name => _contributor.Name;
    }
}
