using System;

namespace Assimalign.Cohesion.LoadBalancer.Hosting;

using Assimalign.Cohesion.Hosting;
using Assimalign.Cohesion.LoadBalancer.Hosting.Internal;

/// <summary>
/// The standalone hosting application for the load balancer resource. Composes the resource's
/// units of work as hosted services, each selecting its execution model per the
/// Assimalign.Cohesion.Hosting per-service execution menu (see docs/DESIGN.md).
/// </summary>
public sealed class LoadBalancerApplication : Host<LoadBalancerApplicationContext>
{
    private readonly LoadBalancerApplicationContext _context;

    /// <summary>
    /// Initializes a new instance of the <see cref="LoadBalancerApplication"/> class.
    /// </summary>
    /// <param name="options">The application options.</param>
    /// <exception cref="ArgumentNullException"><paramref name="options"/> is null.</exception>
    public LoadBalancerApplication(LoadBalancerApplicationOptions options) : base(options)
    {
        ArgumentNullException.ThrowIfNull(options);

        _context = new LoadBalancerApplicationContext(options, new IHostService[]
        {
            new ProxyDataPlaneService(),
        });
    }

    /// <summary>
    /// Gets the application context.
    /// </summary>
    public override LoadBalancerApplicationContext Context => _context;
}