using System;
using System.Collections.Generic;

using Assimalign.Cohesion.Hosting;
using Assimalign.Cohesion.VpnGateway;

namespace Assimalign.Cohesion.VpnGateway.Hosting;

internal sealed class VpnGatewayApplicationBuilder : IVpnGatewayApplicationBuilder
{
    private readonly List<Func<IHostContext, IHostService>> _serviceFactories = [];

    internal VpnGatewayApplicationBuilder(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);
    }

    public IVpnGatewayApplicationBuilder AddService(IHostService service)
    {
        ArgumentNullException.ThrowIfNull(service);

        _serviceFactories.Add(_ => service);
        return this;
    }

    public IVpnGatewayApplicationBuilder AddService(Func<IHostContext, IHostService> factory)
    {
        ArgumentNullException.ThrowIfNull(factory);

        _serviceFactories.Add(factory);
        return this;
    }

    public IVpnGatewayApplication Build()
    {
        var options = new VpnGatewayApplicationOptions();
        var context = new VpnGatewayApplicationContext();
        var hostedServices = new IHostService[_serviceFactories.Count];

        for (var index = 0; index < hostedServices.Length; index++)
        {
            hostedServices[index] = _serviceFactories[index](context)
                ?? throw new InvalidOperationException("A VPN gateway service factory returned null.");
        }

        context.SetHostedServices(hostedServices);

        return new VpnGatewayApplicationHost(options, context);
    }

    IHost IHostBuilder.Build() => Build();
}
