using System;
using System.Reflection;

using Assimalign.Cohesion.LoadBalancer;

namespace Assimalign.Cohesion.LoadBalancer.Hosting;

/// <summary>
/// Creates load balancer application builders.
/// </summary>
public static class LoadBalancerApplication
{
    /// <summary>
    /// Creates a builder for a load balancer application.
    /// </summary>
    /// <param name="args">The command-line arguments supplied to the application.</param>
    /// <returns>A builder for the load balancer application.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="args"/> is <see langword="null"/>.</exception>
    public static ILoadBalancerApplicationBuilder CreateBuilder(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);

        return CreateBuilder(args, Assembly.GetEntryAssembly() ?? typeof(LoadBalancerApplication).Assembly);
    }

    internal static ILoadBalancerApplicationBuilder CreateBuilder(string[] args, Assembly resourceAssembly)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(resourceAssembly);
        return new LoadBalancerApplicationBuilder(args, resourceAssembly);
    }
}
