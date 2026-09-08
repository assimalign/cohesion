using System;
using System.Collections.Generic;

using Assimalign.Cohesion.Hosting;
using Assimalign.Cohesion.LoadBalancer;

namespace Assimalign.Cohesion.LoadBalancer.Hosting;

internal sealed class LoadBalancerApplicationBuilder : ILoadBalancerApplicationBuilder
{
    private readonly List<Func<IHostContext, IHostService>> _serviceRegistrations = new();

    internal LoadBalancerApplicationBuilder(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);
    }

    public ILoadBalancerApplicationBuilder AddService(IHostService service)
    {
        ArgumentNullException.ThrowIfNull(service);

        _serviceRegistrations.Add(_ => service);
        return this;
    }

    public ILoadBalancerApplicationBuilder AddService(Func<IHostContext, IHostService> factory)
    {
        ArgumentNullException.ThrowIfNull(factory);

        _serviceRegistrations.Add(factory);
        return this;
    }

    public ILoadBalancerApplication Build()
    {
        var options = new LoadBalancerApplicationOptions();
        var context = new LoadBalancerApplicationContext();
        var hostedServices = new IHostService[_serviceRegistrations.Count];

        for (int index = 0; index < _serviceRegistrations.Count; index++)
        {
            hostedServices[index] = _serviceRegistrations[index].Invoke(context)
                ?? throw new InvalidOperationException(
                    "The load balancer application service factory returned null.");
        }

        context.SetHostedServices(hostedServices);

        return new LoadBalancerApplicationHost(options, context);
    }

    IHost IHostBuilder.Build() => Build();
}
