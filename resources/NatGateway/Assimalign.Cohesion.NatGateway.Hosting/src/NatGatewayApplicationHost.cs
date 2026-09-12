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

    Task INatGatewayApplication.RunAsync(CancellationToken cancellationToken) =>
        base.RunAsync(cancellationToken);
}
