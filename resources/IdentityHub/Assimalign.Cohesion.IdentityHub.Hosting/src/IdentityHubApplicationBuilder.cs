using System;
using System.Collections.Generic;

using Assimalign.Cohesion.Hosting;
using Assimalign.Cohesion.IdentityHub;

namespace Assimalign.Cohesion.IdentityHub.Hosting;

internal sealed class IdentityHubApplicationBuilder : IIdentityHubApplicationBuilder
{
    private readonly List<Func<IHostContext, IHostService>> _serviceRegistrations = new();

    internal IdentityHubApplicationBuilder(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);
    }

    public IIdentityHubApplicationBuilder AddService(IHostService service)
    {
        ArgumentNullException.ThrowIfNull(service);

        _serviceRegistrations.Add(_ => service);
        return this;
    }

    public IIdentityHubApplicationBuilder AddService(Func<IHostContext, IHostService> factory)
    {
        ArgumentNullException.ThrowIfNull(factory);

        _serviceRegistrations.Add(factory);
        return this;
    }

    public IIdentityHubApplication Build()
    {
        var options = new IdentityHubApplicationOptions();
        var context = new IdentityHubApplicationContext();
        var hostedServices = new IHostService[_serviceRegistrations.Count];

        for (int index = 0; index < _serviceRegistrations.Count; index++)
        {
            hostedServices[index] = _serviceRegistrations[index].Invoke(context)
                ?? throw new InvalidOperationException(
                    "The identity hub application service factory returned null.");
        }

        context.SetHostedServices(hostedServices);

        return new IdentityHubApplicationHost(options, context);
    }

    IHost IHostBuilder.Build() => Build();
}
