using System;
using System.Collections.Generic;

using Assimalign.Cohesion.Hosting;
using Assimalign.Cohesion.ApiManager;

namespace Assimalign.Cohesion.ApiManager.Hosting;

internal sealed class ApiManagerApplicationBuilder : IApiManagerApplicationBuilder
{
    private readonly List<Func<IHostContext, IHostService>> _serviceRegistrations = new();

    internal ApiManagerApplicationBuilder(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);
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
        var context = new ApiManagerApplicationContext();
        var hostedServices = new IHostService[_serviceRegistrations.Count];

        for (int index = 0; index < _serviceRegistrations.Count; index++)
        {
            hostedServices[index] = _serviceRegistrations[index].Invoke(context)
                ?? throw new InvalidOperationException(
                    "The API manager application service factory returned null.");
        }

        context.SetHostedServices(hostedServices);

        return new ApiManagerApplicationHost(options, context);
    }

    IHost IHostBuilder.Build() => Build();
}
