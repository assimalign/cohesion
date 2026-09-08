using System;
using System.Collections.Generic;

using Assimalign.Cohesion.Hosting;
using Assimalign.Cohesion.LogSpace;

namespace Assimalign.Cohesion.LogSpace.Hosting;

internal sealed class LogSpaceApplicationBuilder : ILogSpaceApplicationBuilder
{
    private readonly List<Func<IHostContext, IHostService>> _serviceRegistrations = new();

    internal LogSpaceApplicationBuilder(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);
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
        var context = new LogSpaceApplicationContext();
        var hostedServices = new IHostService[_serviceRegistrations.Count];

        for (int index = 0; index < _serviceRegistrations.Count; index++)
        {
            hostedServices[index] = _serviceRegistrations[index].Invoke(context)
                ?? throw new InvalidOperationException(
                    "The LogSpace application service factory returned null.");
        }

        context.SetHostedServices(hostedServices);

        return new LogSpaceApplicationHost(options, context);
    }

    IHost IHostBuilder.Build() => Build();
}
