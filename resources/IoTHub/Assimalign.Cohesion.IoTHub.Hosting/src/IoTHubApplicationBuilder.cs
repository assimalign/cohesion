using System;
using System.Collections.Generic;
using System.Reflection;

using Assimalign.Cohesion.Hosting;
using Assimalign.Cohesion.Hosting.Resources;
using Assimalign.Cohesion.IoTHub;

namespace Assimalign.Cohesion.IoTHub.Hosting;

internal sealed class IoTHubApplicationBuilder : IIoTHubApplicationBuilder
{
    private readonly List<Func<IHostContext, IHostService>> _serviceRegistrations = new();

    private readonly IResourceControlPlane? _controlPlane;
    private readonly ResourceContext? _resourceContext;

    internal IoTHubApplicationBuilder(string[] args, Assembly resourceAssembly)
    {
        ArgumentNullException.ThrowIfNull(args);
        if (ResourceRuntime.TryCreateControlPlane(resourceAssembly, out IResourceControlPlane? controlPlane))
        {
            _controlPlane = controlPlane ?? throw new InvalidOperationException("The registered IoTHub control-plane factory returned null.");
            _resourceContext = ResourceRuntime.Current;
        }
    }

    public IIoTHubApplicationBuilder AddService(IHostService service)
    {
        ArgumentNullException.ThrowIfNull(service);

        _serviceRegistrations.Add(_ => service);
        return this;
    }

    public IIoTHubApplicationBuilder AddService(Func<IHostContext, IHostService> factory)
    {
        ArgumentNullException.ThrowIfNull(factory);

        _serviceRegistrations.Add(factory);
        return this;
    }

    public IIoTHubApplication Build()
    {
        var options = new IoTHubApplicationOptions();
        var context = new IoTHubApplicationContext(_resourceContext);
        bool hasEndpoint = _controlPlane is not null && _resourceContext!.Endpoints.ContainsKey("http");
        var hostedServices = new IHostService[_serviceRegistrations.Count + (hasEndpoint ? 1 : 0)];

        for (int index = 0; index < _serviceRegistrations.Count; index++)
        {
            hostedServices[index] = _serviceRegistrations[index].Invoke(context)
                ?? throw new InvalidOperationException(
                    "The IoT hub application service factory returned null.");
        }

        if (_controlPlane is not null)
        {
            _controlPlane.AddHealthContributor(context);
            if (hasEndpoint)
            {
                Uri endpoint = _resourceContext!.Endpoints["http"];
                _controlPlane.ObserveEndpoint("http", endpoint);
                hostedServices[^1] = new IoTHubControlPlaneEndpointService(endpoint, _controlPlane, _resourceContext, context);
            }
        }
        context.SetHostedServices(hostedServices);

        var application = new IoTHubApplicationHost(options, context);
        if (_controlPlane is not null)
        {
            ResourceRuntime.HostBuilt(application, _controlPlane);
        }
        return application;
    }

    IHost IHostBuilder.Build() => Build();
}
