using System;

using Assimalign.Cohesion.Hosting;
using Assimalign.Cohesion.VpnGateway;

namespace Assimalign.Cohesion.VpnGateway.Hosting;

internal sealed class VpnGatewayApplicationBuilder : IVpnGatewayApplicationBuilder
{
    internal VpnGatewayApplicationBuilder(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);
    }

    public IVpnGatewayApplication Build()
    {
        var options = new VpnGatewayApplicationOptions();
        var context = new VpnGatewayApplicationContext();

        return new VpnGatewayApplicationHost(options, context);
    }

    IHost IHostBuilder.Build() => Build();
}
