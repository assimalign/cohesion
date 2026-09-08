using System;
using System.Collections.Generic;

using Assimalign.Cohesion.Hosting;
using Assimalign.Cohesion.NotificationHub;

namespace Assimalign.Cohesion.NotificationHub.Hosting;

internal sealed class NotificationHubApplicationBuilder : INotificationHubApplicationBuilder
{
    private readonly List<Func<IHostContext, IHostService>> _serviceFactories = [];

    internal NotificationHubApplicationBuilder(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);
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
        var context = new NotificationHubApplicationContext();
        var hostedServices = new IHostService[_serviceFactories.Count];

        for (var index = 0; index < hostedServices.Length; index++)
        {
            hostedServices[index] = _serviceFactories[index](context)
                ?? throw new InvalidOperationException("A notification hub service factory returned null.");
        }

        context.SetHostedServices(hostedServices);

        return new NotificationHubApplicationHost(options, context);
    }

    IHost IHostBuilder.Build() => Build();
}
