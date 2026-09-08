using System;

using Assimalign.Cohesion.Hosting;

namespace Assimalign.Cohesion.NatGateway;

/// <summary>
/// Defines the contract-only composition seam for a NAT gateway application.
/// </summary>
public interface INatGatewayApplicationBuilder : IHostBuilder
{
    /// <summary>
    /// Adds an existing host service to the NAT gateway application.
    /// </summary>
    /// <param name="service">The service to add.</param>
    /// <returns>This builder.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="service"/> is <see langword="null"/>.</exception>
    INatGatewayApplicationBuilder AddService(IHostService service);

    /// <summary>
    /// Adds a host service factory that is materialized once for each build.
    /// </summary>
    /// <param name="factory">The factory to invoke with the NAT gateway host context.</param>
    /// <returns>This builder.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="factory"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException"><paramref name="factory"/> returns <see langword="null"/>.</exception>
    INatGatewayApplicationBuilder AddService(Func<IHostContext, IHostService> factory);

    /// <summary>
    /// Builds the NAT gateway application.
    /// </summary>
    /// <returns>The configured NAT gateway application.</returns>
    new INatGatewayApplication Build();
}
