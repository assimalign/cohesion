using Assimalign.Cohesion.Hosting;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Net.LoadBalancer;

public static class HostBuilderExtensions
{
    public static IHostBuilder AddLoadBalancer(this IHostBuilder builder)
    {
        return builder;
    }
}
