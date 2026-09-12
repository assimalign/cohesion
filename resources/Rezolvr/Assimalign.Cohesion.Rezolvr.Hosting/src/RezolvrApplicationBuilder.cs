using System;
using System.Collections.Generic;
using System.Reflection;

using Assimalign.Cohesion.Hosting;
using Assimalign.Cohesion.Hosting.Resources;
using Assimalign.Cohesion.Rezolvr;

namespace Assimalign.Cohesion.Rezolvr.Hosting;

internal sealed class RezolvrApplicationBuilder : IRezolvrApplicationBuilder
{
    private readonly List<Func<IHostContext, IHostService>> _serviceFactories = [];

    private readonly IResourceControlPlane? _controlPlane;
    private readonly ResourceContext? _resourceContext;

    internal RezolvrApplicationBuilder(string[] args, Assembly resourceAssembly)
    {
        ArgumentNullException.ThrowIfNull(args);
        if (ResourceRuntime.TryCreateControlPlane(resourceAssembly, out IResourceControlPlane? controlPlane))
        {
            _controlPlane = controlPlane ?? throw new InvalidOperationException("The registered Rezolvr control-plane factory returned null.");
            _resourceContext = ResourceRuntime.Current;
        }
    }

    public IRezolvrApplicationBuilder AddService(IHostService service)
    {
        ArgumentNullException.ThrowIfNull(service);

        _serviceFactories.Add(_ => service);
        return this;
    }

    public IRezolvrApplicationBuilder AddService(Func<IHostContext, IHostService> factory)
    {
        ArgumentNullException.ThrowIfNull(factory);

        _serviceFactories.Add(factory);
        return this;
    }

    public IRezolvrApplication Build()
    {
        var options = new RezolvrApplicationOptions();
        var context = new RezolvrApplicationContext(_resourceContext);
        bool hasEndpoint = _controlPlane is not null && _resourceContext!.Endpoints.ContainsKey("admin");
        var hostedServices = new IHostService[_serviceFactories.Count + (hasEndpoint ? 1 : 0)];

        for (var index = 0; index < _serviceFactories.Count; index++)
        {
            hostedServices[index] = _serviceFactories[index](context)
                ?? throw new InvalidOperationException("A resolver service factory returned null.");
        }

        if (_controlPlane is not null)
        {
            _controlPlane.AddHealthContributor(context);
            if (hasEndpoint)
            {
                Uri endpoint = _resourceContext!.Endpoints["admin"];
                _controlPlane.ObserveEndpoint("admin", endpoint);
                hostedServices[^1] = new RezolvrControlPlaneEndpointService(endpoint, _controlPlane, _resourceContext, context);
            }
        }
        context.SetHostedServices(hostedServices);

        var application = new RezolvrApplicationHost(options, context);
        if (_controlPlane is not null)
        {
            ResourceRuntime.HostBuilt(application, _controlPlane);
        }
        return application;
    }

    IHost IHostBuilder.Build() => Build();
}
