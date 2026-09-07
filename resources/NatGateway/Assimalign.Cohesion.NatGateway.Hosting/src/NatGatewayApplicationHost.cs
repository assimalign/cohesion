using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Hosting;
using Assimalign.Cohesion.NatGateway;

namespace Assimalign.Cohesion.NatGateway.Hosting;

internal sealed class NatGatewayApplicationHost : Host<NatGatewayApplicationContext>, INatGatewayApplication
{
    private readonly NatGatewayApplicationContext _context;

    internal NatGatewayApplicationHost(
        NatGatewayApplicationOptions options,
        NatGatewayApplicationContext context)
        : base(options)
    {
        _context = context;
    }

    public override NatGatewayApplicationContext Context => _context;

    async Task INatGatewayApplication.RunAsync(CancellationToken cancellationToken)
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
