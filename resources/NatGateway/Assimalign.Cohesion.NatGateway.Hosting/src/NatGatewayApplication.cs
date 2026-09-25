using System;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Hosting;
using Assimalign.Cohesion.NatGateway;
using Assimalign.Cohesion.NatGateway.Hosting.Internal;

namespace Assimalign.Cohesion.NatGateway.Hosting;

/// <summary>
/// Hosts a NatGateway application and its ordered service lifecycle.
/// </summary>
public sealed class NatGatewayApplication : Host<NatGatewayApplicationContext>, INatGatewayApplication
{
    private readonly NatGatewayApplicationContext _context;

    internal NatGatewayApplication(
        NatGatewayApplicationOptions options,
        NatGatewayApplicationContext context)
        : base(options)
    {
        _context = context;
    }

    /// <summary>
    /// Gets the concrete application context.
    /// </summary>
    public override NatGatewayApplicationContext Context => _context;

    INatGatewayApplicationContext INatGatewayApplication.Context => _context;

    Task INatGatewayApplication.StartAsync(CancellationToken cancellationToken) =>
        ((IHost)this).StartAsync(cancellationToken);

    Task INatGatewayApplication.StopAsync(CancellationToken cancellationToken) =>
        ((IHost)this).StopAsync(cancellationToken);

    /// <summary>
    /// Creates a builder for a NAT gateway application.
    /// </summary>
    /// <param name="args">The command-line arguments supplied to the application.</param>
    /// <returns>A builder for the NAT gateway application.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="args"/> is <see langword="null"/>.</exception>
    public static NatGatewayApplicationBuilder CreateBuilder(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);

        return CreateBuilder(args, Assembly.GetEntryAssembly() ?? typeof(NatGatewayApplication).Assembly);
    }

    internal static NatGatewayApplicationBuilder CreateBuilder(string[] args, Assembly resourceAssembly)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(resourceAssembly);
        return new NatGatewayApplicationBuilder(args, resourceAssembly);
    }
}
