using System;
using System.Collections.Generic;
using System.Reflection;

using Assimalign.Cohesion.Hosting;
using Assimalign.Cohesion.Hosting.Resources;
using Assimalign.Cohesion.NotificationHub;

namespace Assimalign.Cohesion.NotificationHub.Hosting;

internal sealed class NotificationHubApplicationBuilder : INotificationHubApplicationBuilder
{
    private readonly List<Func<IHostContext, IHostService>> _serviceFactories = [];

    private readonly IResourceControlPlane? _controlPlane;
    private readonly ResourceContext? _resourceContext;

    internal NotificationHubApplicationBuilder(string[] args, Assembly resourceAssembly)
    {
        ArgumentNullException.ThrowIfNull(args);
        if (ResourceRuntime.TryCreateControlPlane(resourceAssembly, out IResourceControlPlane? controlPlane))
        {
            _controlPlane = controlPlane ?? throw new InvalidOperationException("The registered NotificationHub control-plane factory returned null.");
            _resourceContext = ResourceRuntime.Current;
        }
    }

    public INotificationHubApplicationBuilder AddService(IHostService service)
    {
        ArgumentNullException.ThrowIfNull(service);

        _serviceFactories.Add(_ => service);
        return this;
    }

    public INotificationHubApplicationBuilder AddService(Func<IHostContext, IHostService> factory)
    {
        ArgumentNullException.ThrowIfNull(factory);

        _serviceFactories.Add(factory);
        return this;
    }

    public INotificationHubApplication Build()
    {
        var options = new NotificationHubApplicationOptions();
        var context = new NotificationHubApplicationContext(_resourceContext);
        bool hasEndpoint = _controlPlane is not null && _resourceContext!.Endpoints.ContainsKey("http");
        var hostedServices = new IHostService[_serviceFactories.Count + (hasEndpoint ? 1 : 0)];

        for (var index = 0; index < _serviceFactories.Count; index++)
        {
            hostedServices[index] = _serviceFactories[index](context)
                ?? throw new InvalidOperationException("A notification hub service factory returned null.");
        }

        if (_controlPlane is not null)
        {
            _controlPlane.AddHealthContributor(context);
            if (hasEndpoint)
            {
                Uri endpoint = _resourceContext!.Endpoints["http"];
                _controlPlane.ObserveEndpoint("http", endpoint);
                hostedServices[^1] = new NotificationHubControlPlaneEndpointService(endpoint, _controlPlane, _resourceContext, context);
            }
        }
        context.SetHostedServices(hostedServices);

        var application = new NotificationHubApplicationHost(options, context);
        if (_controlPlane is not null)
        {
            ResourceRuntime.HostBuilt(application, _controlPlane);
        }
        return application;
    }

    IHost IHostBuilder.Build() => Build();
}
