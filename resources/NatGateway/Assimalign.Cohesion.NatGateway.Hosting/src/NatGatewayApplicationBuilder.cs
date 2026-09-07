using System;

using Assimalign.Cohesion.Hosting;
using Assimalign.Cohesion.NatGateway;

namespace Assimalign.Cohesion.NatGateway.Hosting;

internal sealed class NatGatewayApplicationBuilder : INatGatewayApplicationBuilder
{
    internal NatGatewayApplicationBuilder(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);
    }

    public INatGatewayApplication Build()
    {
        var options = new NatGatewayApplicationOptions();
        var context = new NatGatewayApplicationContext();

        return new NatGatewayApplicationHost(options, context);
    }

    IHost IHostBuilder.Build() => Build();
}
