using System;
using System.Collections.Generic;

using Assimalign.Cohesion.Hosting;
using Assimalign.Cohesion.Rezolvr;

namespace Assimalign.Cohesion.Rezolvr.Hosting;

internal sealed class RezolvrApplicationBuilder : IRezolvrApplicationBuilder
{
    private readonly List<Func<IHostContext, IHostService>> _serviceFactories = [];

    internal RezolvrApplicationBuilder(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);
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
        var context = new RezolvrApplicationContext();
        var hostedServices = new IHostService[_serviceFactories.Count];

        for (var index = 0; index < hostedServices.Length; index++)
        {
            hostedServices[index] = _serviceFactories[index](context)
                ?? throw new InvalidOperationException("A resolver service factory returned null.");
        }

        context.SetHostedServices(hostedServices);

        return new RezolvrApplicationHost(options, context);
    }

    IHost IHostBuilder.Build() => Build();
}
