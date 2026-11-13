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
        var observation = await _context.GetHealthAsync(Name, cancellationToken).ConfigureAwait(false);
        return new ApplicationInstanceHealthContribution(observation.Status switch
        {
            DatabaseHealthStatus.Healthy => ApplicationInstanceHealthStatus.Healthy,
            DatabaseHealthStatus.Degraded => ApplicationInstanceHealthStatus.Degraded,
            _ => ApplicationInstanceHealthStatus.Unhealthy,
        }, observation.Description, observation.Data);
    }

    private sealed class DebugView
    {
        private readonly DatabaseHealthContributor _contributor;

        public DebugView(DatabaseHealthContributor contributor) { _contributor = contributor; }

        public string Name => _contributor.Name;
    }
}
