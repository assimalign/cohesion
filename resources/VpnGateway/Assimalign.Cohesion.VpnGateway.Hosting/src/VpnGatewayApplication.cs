using System;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Hosting;
using Assimalign.Cohesion.VpnGateway;
using Assimalign.Cohesion.VpnGateway.Hosting.Internal;

namespace Assimalign.Cohesion.VpnGateway.Hosting;

/// <summary>
/// Hosts a VpnGateway application and its ordered service lifecycle.
/// </summary>
public sealed class VpnGatewayApplication : Host<VpnGatewayApplicationContext>, IVpnGatewayApplication
{
    private readonly VpnGatewayApplicationContext _context;

    internal VpnGatewayApplication(
        VpnGatewayApplicationOptions options,
        VpnGatewayApplicationContext context)
        : base(options)
    {
        _context = context;
    }

    /// <summary>
    /// Gets the concrete application context.
    /// </summary>
    public override VpnGatewayApplicationContext Context => _context;

    IVpnGatewayApplicationContext IVpnGatewayApplication.Context => _context;

    Task IVpnGatewayApplication.StartAsync(CancellationToken cancellationToken) =>
        ((IHost)this).StartAsync(cancellationToken);

    Task IVpnGatewayApplication.StopAsync(CancellationToken cancellationToken) =>
        ((IHost)this).StopAsync(cancellationToken);

    /// <summary>
    /// Creates a builder for a VPN gateway application.
    /// </summary>
    /// <param name="args">The command-line arguments supplied to the application.</param>
    /// <returns>A builder for the VPN gateway application.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="args"/> is <see langword="null"/>.</exception>
    public static VpnGatewayApplicationBuilder CreateBuilder(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);

        return CreateBuilder(args, Assembly.GetEntryAssembly() ?? typeof(VpnGatewayApplication).Assembly);
    }

    internal static VpnGatewayApplicationBuilder CreateBuilder(string[] args, Assembly resourceAssembly)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(resourceAssembly);
        return new VpnGatewayApplicationBuilder(args, resourceAssembly);
    }
}
