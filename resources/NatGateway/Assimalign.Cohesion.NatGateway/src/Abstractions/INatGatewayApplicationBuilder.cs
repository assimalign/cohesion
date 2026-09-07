using Assimalign.Cohesion.Hosting;

namespace Assimalign.Cohesion.NatGateway;

/// <summary>
/// Defines the contract-only composition seam for a NAT gateway application.
/// </summary>
public interface INatGatewayApplicationBuilder : IHostBuilder
{
    /// <summary>
    /// Builds the NAT gateway application.
    /// </summary>
    /// <returns>The configured NAT gateway application.</returns>
    new INatGatewayApplication Build();
}
