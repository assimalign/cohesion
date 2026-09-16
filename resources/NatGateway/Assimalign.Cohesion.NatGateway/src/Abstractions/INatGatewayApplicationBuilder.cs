namespace Assimalign.Cohesion.NatGateway;

/// <summary>
/// Defines the contract-only composition seam for a NAT gateway application.
/// </summary>
public interface INatGatewayApplicationBuilder
{
    /// <summary>
    /// Builds the NAT gateway application.
    /// </summary>
    /// <returns>The configured NAT gateway application.</returns>
    INatGatewayApplication Build();
}
