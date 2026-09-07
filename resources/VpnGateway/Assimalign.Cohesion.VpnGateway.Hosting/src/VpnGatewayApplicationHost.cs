using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Hosting;
using Assimalign.Cohesion.VpnGateway;

namespace Assimalign.Cohesion.VpnGateway.Hosting;

internal sealed class VpnGatewayApplicationHost : Host<VpnGatewayApplicationContext>, IVpnGatewayApplication
{
    private readonly VpnGatewayApplicationContext _context;

    internal VpnGatewayApplicationHost(
        VpnGatewayApplicationOptions options,
        VpnGatewayApplicationContext context)
        : base(options)
    {
        _context = context;
    }

    public override VpnGatewayApplicationContext Context => _context;

    async Task IVpnGatewayApplication.RunAsync(CancellationToken cancellationToken)
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
