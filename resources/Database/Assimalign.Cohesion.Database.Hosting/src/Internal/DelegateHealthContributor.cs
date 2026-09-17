using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Hosting.Health;

namespace Assimalign.Cohesion.Database.Hosting;

internal sealed class DelegateHealthContributor : IHealthContributor
{
    private readonly ResourceHealthCheck _check;

    internal DelegateHealthContributor(string name, ResourceHealthCheck check)
    {
        Name = name;
        _check = check;
    }

    public string Name { get; }

    public ValueTask<HealthContribution> CheckAsync(CancellationToken cancellationToken = default)
    {
        return _check.Invoke(cancellationToken);
    }
}
