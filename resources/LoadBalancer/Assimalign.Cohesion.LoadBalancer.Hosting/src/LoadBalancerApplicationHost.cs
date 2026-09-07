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

    async Task ILoadBalancerApplication.RunAsync(CancellationToken cancellationToken)
    {
        // TODO(design item 12): Route RunAsync through ResourceRuntime once the ambient runtime seam exists.
        if (cancellationToken.IsCancellationRequested)
        {
            await ((IHost)this).StartAsync(CancellationToken.None).ConfigureAwait(false);
            await ((IHost)this).StopAsync(CancellationToken.None).ConfigureAwait(false);
            return;
        }

        await base.RunAsync(cancellationToken).ConfigureAwait(false);
    }
}
