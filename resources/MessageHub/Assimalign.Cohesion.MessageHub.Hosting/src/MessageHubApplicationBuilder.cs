using System;
using System.Collections.Generic;

using Assimalign.Cohesion.Hosting;
using Assimalign.Cohesion.MessageHub;

namespace Assimalign.Cohesion.MessageHub.Hosting;

internal sealed class MessageHubApplicationBuilder : IMessageHubApplicationBuilder
{
    private readonly List<Func<IHostContext, IHostService>> _serviceFactories = [];

    internal MessageHubApplicationBuilder(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);
    }

    public IMessageHubApplicationBuilder AddService(IHostService service)
    {
        ArgumentNullException.ThrowIfNull(service);

        _serviceFactories.Add(_ => service);
        return this;
    }

    public IMessageHubApplicationBuilder AddService(Func<IHostContext, IHostService> factory)
    {
        ArgumentNullException.ThrowIfNull(factory);

        _serviceFactories.Add(factory);
        return this;
    }

    public IMessageHubApplication Build()
    {
        var options = new MessageHubApplicationOptions();
        var context = new MessageHubApplicationContext();
        var hostedServices = new IHostService[_serviceFactories.Count];

        for (var index = 0; index < hostedServices.Length; index++)
        {
            hostedServices[index] = _serviceFactories[index](context)
                ?? throw new InvalidOperationException("A message hub service factory returned null.");
        }

        context.SetHostedServices(hostedServices);

        return new MessageHubApplicationHost(options, context);
    }

    IHost IHostBuilder.Build() => Build();
}
