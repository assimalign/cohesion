using System;
using System.Collections.Generic;
using System.Reflection;

using Assimalign.Cohesion.Hosting;
using Assimalign.Cohesion.Hosting.Resources;
using Assimalign.Cohesion.LogSpace;

namespace Assimalign.Cohesion.LogSpace.Hosting;

internal sealed class LogSpaceApplicationBuilder : ILogSpaceApplicationBuilder
{
    private readonly List<Func<IHostContext, IHostService>> _serviceRegistrations = new();

    private readonly IResourceControlPlane? _controlPlane;
    private readonly ResourceContext? _resourceContext;

    internal LogSpaceApplicationBuilder(string[] args, Assembly resourceAssembly)
    {
        ArgumentNullException.ThrowIfNull(args);
        if (ResourceRuntime.TryCreateControlPlane(resourceAssembly, out IResourceControlPlane? controlPlane))
        {
            _controlPlane = controlPlane ?? throw new InvalidOperationException("The registered LogSpace control-plane factory returned null.");
            _resourceContext = ResourceRuntime.Current;
        }
    }

    public ILogSpaceApplicationBuilder AddService(IHostService service)
    {
        ArgumentNullException.ThrowIfNull(service);

        _serviceRegistrations.Add(_ => service);
        return this;
    }

    public ILogSpaceApplicationBuilder AddService(Func<IHostContext, IHostService> factory)
    {
        ArgumentNullException.ThrowIfNull(factory);

        _serviceRegistrations.Add(factory);
        return this;
    }

    public ILogSpaceApplication Build()
    {
        var options = new LogSpaceApplicationOptions();
        var context = new LogSpaceApplicationContext(_resourceContext);
        bool hasEndpoint = _controlPlane is not null && _resourceContext!.Endpoints.ContainsKey("query");
        var hostedServices = new IHostService[_serviceRegistrations.Count + (hasEndpoint ? 1 : 0)];

        for (int index = 0; index < _serviceRegistrations.Count; index++)
        {
            hostedServices[index] = _serviceRegistrations[index].Invoke(context)
                ?? throw new InvalidOperationException(
                    "The LogSpace application service factory returned null.");
        }

        if (_controlPlane is not null)
        {
            _controlPlane.AddHealthContributor(context);
            if (hasEndpoint)
            {
                Uri endpoint = _resourceContext!.Endpoints["query"];
                _controlPlane.ObserveEndpoint("query", endpoint);
                hostedServices[^1] = new LogSpaceControlPlaneEndpointService(endpoint, _controlPlane, _resourceContext, context);
            }
        }
        context.SetHostedServices(hostedServices);

        var application = new LogSpaceApplicationHost(options, context);
        if (_controlPlane is not null)
        {
            ResourceRuntime.HostBuilt(application, _controlPlane);
        }
        return application;
    }

    IHost IHostBuilder.Build() => Build();
}
