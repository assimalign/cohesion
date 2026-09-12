using System;
using System.Reflection;

using Assimalign.Cohesion.NatGateway;

namespace Assimalign.Cohesion.NatGateway.Hosting;

/// <summary>
/// Creates NAT gateway application builders.
/// </summary>
public static class NatGatewayApplication
{
    /// <summary>
    /// Creates a builder for a NAT gateway application.
    /// </summary>
    /// <param name="args">The command-line arguments supplied to the application.</param>
    /// <returns>A builder for the NAT gateway application.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="args"/> is <see langword="null"/>.</exception>
    public static INatGatewayApplicationBuilder CreateBuilder(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);

        return CreateBuilder(args, Assembly.GetEntryAssembly() ?? typeof(NatGatewayApplication).Assembly);
    }

    internal static INatGatewayApplicationBuilder CreateBuilder(string[] args, Assembly resourceAssembly)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(resourceAssembly);
        return new NatGatewayApplicationBuilder(args, resourceAssembly);
    }
}
