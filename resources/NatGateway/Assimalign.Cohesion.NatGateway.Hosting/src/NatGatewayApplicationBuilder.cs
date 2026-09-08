using System;
using System.Collections.Generic;

using Assimalign.Cohesion.Hosting;
using Assimalign.Cohesion.NatGateway;

namespace Assimalign.Cohesion.NatGateway.Hosting;

internal sealed class NatGatewayApplicationBuilder : INatGatewayApplicationBuilder
{
    private readonly List<Func<IHostContext, IHostService>> _serviceFactories = [];

    internal NatGatewayApplicationBuilder(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);
    }

    public INatGatewayApplicationBuilder AddService(IHostService service)
    {
        ArgumentNullException.ThrowIfNull(service);

        _serviceFactories.Add(_ => service);
        return this;
    }

    public INatGatewayApplicationBuilder AddService(Func<IHostContext, IHostService> factory)
    {
        ArgumentNullException.ThrowIfNull(factory);

        _serviceFactories.Add(factory);
        return this;
    }

    public INatGatewayApplication Build()
    {
        var options = new NatGatewayApplicationOptions();
        var context = new NatGatewayApplicationContext();
        var hostedServices = new IHostService[_serviceFactories.Count];

        for (var index = 0; index < hostedServices.Length; index++)
        {
            hostedServices[index] = _serviceFactories[index](context)
                ?? throw new InvalidOperationException("A NAT gateway service factory returned null.");
        }

        context.SetHostedServices(hostedServices);

        return new NatGatewayApplicationHost(options, context);
    }

    IHost IHostBuilder.Build() => Build();
}
