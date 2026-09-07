using Assimalign.Cohesion.Hosting;

namespace Assimalign.Cohesion.LoadBalancer;

/// <summary>
/// Defines the contract-only composition seam for a load balancer application.
/// </summary>
public interface ILoadBalancerApplicationBuilder : IHostBuilder
{
    /// <summary>
    /// Builds the load balancer application.
    /// </summary>
    /// <returns>The configured load balancer application.</returns>
    new ILoadBalancerApplication Build();
}
