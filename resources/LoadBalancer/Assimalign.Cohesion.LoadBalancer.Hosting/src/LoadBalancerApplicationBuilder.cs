using System;

using Assimalign.Cohesion.Hosting;
using Assimalign.Cohesion.LoadBalancer;

namespace Assimalign.Cohesion.LoadBalancer.Hosting;

internal sealed class LoadBalancerApplicationBuilder : ILoadBalancerApplicationBuilder
{
    internal LoadBalancerApplicationBuilder(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);
    }

    public ILoadBalancerApplication Build()
    {
        var options = new LoadBalancerApplicationOptions();
        var context = new LoadBalancerApplicationContext();

        return new LoadBalancerApplicationHost(options, context);
    }

    IHost IHostBuilder.Build() => Build();
}
