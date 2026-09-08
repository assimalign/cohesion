using System;

using Assimalign.Cohesion.Hosting;

namespace Assimalign.Cohesion.VpnGateway;

/// <summary>
/// Defines the contract-only composition seam for a VPN gateway application.
/// </summary>
public interface IVpnGatewayApplicationBuilder : IHostBuilder
{
    /// <summary>
    /// Adds an existing host service to the VPN gateway application.
    /// </summary>
    /// <param name="service">The service to add.</param>
    /// <returns>This builder.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="service"/> is <see langword="null"/>.</exception>
    IVpnGatewayApplicationBuilder AddService(IHostService service);

    /// <summary>
    /// Adds a host service factory that is materialized once for each build.
    /// </summary>
    /// <param name="factory">The factory to invoke with the VPN gateway host context.</param>
    /// <returns>This builder.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="factory"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException"><paramref name="factory"/> returns <see langword="null"/>.</exception>
    IVpnGatewayApplicationBuilder AddService(Func<IHostContext, IHostService> factory);

    /// <summary>
    /// Builds the VPN gateway application.
    /// </summary>
    /// <returns>The configured VPN gateway application.</returns>
    new IVpnGatewayApplication Build();
}
