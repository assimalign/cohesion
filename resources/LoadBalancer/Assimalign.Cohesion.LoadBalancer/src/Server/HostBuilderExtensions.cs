using System;

namespace Assimalign.Cohesion.LoadBalancer;

using Assimalign.Cohesion.Hosting;

public static class HostBuilderExtensions
{
    extension(IHostBuilder builder)
    {
        /// <summary>
        /// Adds the load balancer resource to the host.
        /// </summary>
        /// <returns>The host builder for chaining.</returns>
        public IHostBuilder AddLoadBalancer()
        {
            return builder;
        }
    }
}
