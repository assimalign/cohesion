using System;
using System.Collections.Generic;

using Assimalign.Cohesion.Hosting;
using Assimalign.Cohesion.EventHub;

namespace Assimalign.Cohesion.EventHub.Hosting;

internal sealed class EventHubApplicationBuilder : IEventHubApplicationBuilder
{
    private readonly List<Func<IHostContext, IHostService>> _serviceRegistrations = new();

    internal EventHubApplicationBuilder(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);
    }

    public IEventHubApplicationBuilder AddService(IHostService service)
    {
        ArgumentNullException.ThrowIfNull(service);

        _serviceRegistrations.Add(_ => service);
        return this;
    }

    public IEventHubApplicationBuilder AddService(Func<IHostContext, IHostService> factory)
    {
        ArgumentNullException.ThrowIfNull(factory);

        _serviceRegistrations.Add(factory);
        return this;
    }

    public IEventHubApplication Build()
    {
        var options = new EventHubApplicationOptions();
        var context = new EventHubApplicationContext();
        var hostedServices = new IHostService[_serviceRegistrations.Count];

        for (int index = 0; index < _serviceRegistrations.Count; index++)
        {
            hostedServices[index] = _serviceRegistrations[index].Invoke(context)
                ?? throw new InvalidOperationException(
                    "The event hub application service factory returned null.");
        }

        context.SetHostedServices(hostedServices);

        return new EventHubApplicationHost(options, context);
    }

    IHost IHostBuilder.Build() => Build();
}
