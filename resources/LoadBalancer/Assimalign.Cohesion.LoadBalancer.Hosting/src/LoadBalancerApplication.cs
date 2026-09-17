using System;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Hosting;
using Assimalign.Cohesion.LoadBalancer;

namespace Assimalign.Cohesion.LoadBalancer.Hosting;

/// <summary>
/// Hosts a LoadBalancer application and its ordered service lifecycle.
/// </summary>
public sealed class LoadBalancerApplication : Host<LoadBalancerApplicationContext>, ILoadBalancerApplication
{
    private readonly LoadBalancerApplicationContext _context;

    internal LoadBalancerApplication(
        LoadBalancerApplicationOptions options,
        LoadBalancerApplicationContext context)
        : base(options)
    {
        _context = context;
    }

    /// <summary>
    /// Gets the concrete application context.
    /// </summary>
    public override LoadBalancerApplicationContext Context => _context;

    ILoadBalancerApplicationContext ILoadBalancerApplication.Context => _context;

    Task ILoadBalancerApplication.StartAsync(CancellationToken cancellationToken) =>
        ((IHost)this).StartAsync(cancellationToken);

    Task ILoadBalancerApplication.StopAsync(CancellationToken cancellationToken) =>
        ((IHost)this).StopAsync(cancellationToken);

    /// <summary>
    /// Creates a builder for a load balancer application.
    /// </summary>
    /// <param name="args">The command-line arguments supplied to the application.</param>
    /// <returns>A builder for the load balancer application.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="args"/> is <see langword="null"/>.</exception>
    public static LoadBalancerApplicationBuilder CreateBuilder(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);

        return CreateBuilder(args, Assembly.GetEntryAssembly() ?? typeof(LoadBalancerApplication).Assembly);
    }

    internal static LoadBalancerApplicationBuilder CreateBuilder(string[] args, Assembly resourceAssembly)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(resourceAssembly);
        return new LoadBalancerApplicationBuilder(args, resourceAssembly);
    }
}
