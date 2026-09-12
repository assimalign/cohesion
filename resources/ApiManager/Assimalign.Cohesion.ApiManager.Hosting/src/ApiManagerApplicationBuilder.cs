using System;
using System.Collections.Generic;
using System.Reflection;

using Assimalign.Cohesion.ApiManager;
using Assimalign.Cohesion.Hosting;
using Assimalign.Cohesion.Hosting.Resources;

namespace Assimalign.Cohesion.ApiManager.Hosting;

internal sealed class ApiManagerApplicationBuilder : IApiManagerApplicationBuilder
{
    private readonly List<Func<IHostContext, IHostService>> _serviceRegistrations = new();

    private readonly IResourceControlPlane? _controlPlane;
    private readonly ResourceContext? _resourceContext;

    internal ApiManagerApplicationBuilder(string[] args, Assembly resourceAssembly)
    {
        ArgumentNullException.ThrowIfNull(args);
        if (ResourceRuntime.TryCreateControlPlane(resourceAssembly, out IResourceControlPlane? controlPlane))
        {
            _controlPlane = controlPlane ?? throw new InvalidOperationException("The registered ApiManager control-plane factory returned null.");
            _resourceContext = ResourceRuntime.Current;
        }
    }

    public IApiManagerApplicationBuilder AddService(IHostService service)
    {
        ArgumentNullException.ThrowIfNull(service);

        _serviceRegistrations.Add(_ => service);
        return this;
    }

    public IApiManagerApplicationBuilder AddService(Func<IHostContext, IHostService> factory)
    {
        ArgumentNullException.ThrowIfNull(factory);

        _serviceRegistrations.Add(factory);
        return this;
    }

    public IApiManagerApplication Build()
    {
        var options = new ApiManagerApplicationOptions();
        var context = new ApiManagerApplicationContext(_resourceContext);
        bool hasEndpoint = _controlPlane is not null && _resourceContext!.Endpoints.ContainsKey("http");
        var hostedServices = new IHostService[_serviceRegistrations.Count + (hasEndpoint ? 1 : 0)];

        for (int index = 0; index < _serviceRegistrations.Count; index++)
        {
            hostedServices[index] = _serviceRegistrations[index].Invoke(context)
                ?? throw new InvalidOperationException(
                    "The API manager application service factory returned null.");
        }

        if (_controlPlane is not null)
        {
            _controlPlane.AddHealthContributor(context);
            if (hasEndpoint)
            {
                Uri endpoint = _resourceContext!.Endpoints["http"];
                _controlPlane.ObserveEndpoint("http", endpoint);
                hostedServices[^1] = new ApiManagerControlPlaneEndpointService(endpoint, _controlPlane, _resourceContext, context);
            }
        }
        context.SetHostedServices(hostedServices);

        var application = new ApiManagerApplicationHost(options, context);
        if (_controlPlane is not null)
        {
            ResourceRuntime.HostBuilt(application, _controlPlane);
        }
        return application;
    }

    IHost IHostBuilder.Build() => Build();
}
