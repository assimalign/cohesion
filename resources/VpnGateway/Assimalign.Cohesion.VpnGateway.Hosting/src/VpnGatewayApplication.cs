using System;
using System.Reflection;

using Assimalign.Cohesion.VpnGateway;

namespace Assimalign.Cohesion.VpnGateway.Hosting;

/// <summary>
/// Creates VPN gateway application builders.
/// </summary>
public static class VpnGatewayApplication
{
    /// <summary>
    /// Creates a builder for a VPN gateway application.
    /// </summary>
    /// <param name="args">The command-line arguments supplied to the application.</param>
    /// <returns>A builder for the VPN gateway application.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="args"/> is <see langword="null"/>.</exception>
    public static IVpnGatewayApplicationBuilder CreateBuilder(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);

        return CreateBuilder(args, Assembly.GetEntryAssembly() ?? typeof(VpnGatewayApplication).Assembly);
    }

    internal static IVpnGatewayApplicationBuilder CreateBuilder(string[] args, Assembly resourceAssembly)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(resourceAssembly);
        return new VpnGatewayApplicationBuilder(args, resourceAssembly);
    }
}
