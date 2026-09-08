using System;
using System.Collections.Generic;

using Assimalign.Cohesion.Hosting;
using Assimalign.Cohesion.IoTHub;

namespace Assimalign.Cohesion.IoTHub.Hosting;

internal sealed class IoTHubApplicationBuilder : IIoTHubApplicationBuilder
{
    private readonly List<Func<IHostContext, IHostService>> _serviceRegistrations = new();

    internal IoTHubApplicationBuilder(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);
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
        var context = new IoTHubApplicationContext();
        var hostedServices = new IHostService[_serviceRegistrations.Count];

        for (int index = 0; index < _serviceRegistrations.Count; index++)
        {
            hostedServices[index] = _serviceRegistrations[index].Invoke(context)
                ?? throw new InvalidOperationException(
                    "The IoT hub application service factory returned null.");
        }

        context.SetHostedServices(hostedServices);

        return new IoTHubApplicationHost(options, context);
    }

    IHost IHostBuilder.Build() => Build();
}
