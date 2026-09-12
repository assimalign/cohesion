using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Hosting;
using Assimalign.Cohesion.LoadBalancer;

namespace Assimalign.Cohesion.LoadBalancer.Hosting;

internal sealed class LoadBalancerApplicationHost : Host<LoadBalancerApplicationContext>, ILoadBalancerApplication
{
    private readonly LoadBalancerApplicationContext _context;

    internal LoadBalancerApplicationHost(
        LoadBalancerApplicationOptions options,
        LoadBalancerApplicationContext context)
        : base(options)
    {
        _context = context;
    }

    public override LoadBalancerApplicationContext Context => _context;

    Task ILoadBalancerApplication.RunAsync(CancellationToken cancellationToken) =>
        base.RunAsync(cancellationToken);
}
