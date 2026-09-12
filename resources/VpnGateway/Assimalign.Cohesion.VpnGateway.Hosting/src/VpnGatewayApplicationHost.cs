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

    Task IVpnGatewayApplication.RunAsync(CancellationToken cancellationToken) =>
        base.RunAsync(cancellationToken);
}
