using System;
using System.Collections.Generic;
using System.Reflection;

using Assimalign.Cohesion.Hosting;
using Assimalign.Cohesion.Hosting.Resources;
using Assimalign.Cohesion.NatGateway;

namespace Assimalign.Cohesion.NatGateway.Hosting;

internal sealed class NatGatewayApplicationBuilder : INatGatewayApplicationBuilder
{
    private readonly List<Func<IHostContext, IHostService>> _serviceFactories = [];

    private readonly IResourceControlPlane? _controlPlane;
    private readonly ResourceContext? _resourceContext;

    internal NatGatewayApplicationBuilder(string[] args, Assembly resourceAssembly)
    {
        ArgumentNullException.ThrowIfNull(args);
        if (ResourceRuntime.TryCreateControlPlane(resourceAssembly, out IResourceControlPlane? controlPlane))
        {
            _controlPlane = controlPlane ?? throw new InvalidOperationException("The registered NatGateway control-plane factory returned null.");
            _resourceContext = ResourceRuntime.Current;
        }
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
        var context = new NatGatewayApplicationContext(_resourceContext);
        bool hasEndpoint = _controlPlane is not null && _resourceContext!.Endpoints.ContainsKey("http");
        var hostedServices = new IHostService[_serviceFactories.Count + (hasEndpoint ? 1 : 0)];

        for (var index = 0; index < _serviceFactories.Count; index++)
        {
            hostedServices[index] = _serviceFactories[index](context)
                ?? throw new InvalidOperationException("A NAT gateway service factory returned null.");
        }

        if (_controlPlane is not null)
        {
            _controlPlane.AddHealthContributor(context);
            if (hasEndpoint)
            {
                Uri endpoint = _resourceContext!.Endpoints["http"];
                _controlPlane.ObserveEndpoint("http", endpoint);
                hostedServices[^1] = new NatGatewayControlPlaneEndpointService(endpoint, _controlPlane, _resourceContext, context);
            }
        }
        context.SetHostedServices(hostedServices);

        var application = new NatGatewayApplicationHost(options, context);
        if (_controlPlane is not null)
        {
            ResourceRuntime.HostBuilt(application, _controlPlane);
        }
        return application;
    }

    IHost IHostBuilder.Build() => Build();
}
