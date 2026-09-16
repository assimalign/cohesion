namespace Assimalign.Cohesion.VpnGateway;

/// <summary>
/// Defines the contract-only composition seam for a VPN gateway application.
/// </summary>
public interface IVpnGatewayApplicationBuilder
{
    /// <summary>
    /// Builds the VPN gateway application.
    /// </summary>
    /// <returns>The configured VPN gateway application.</returns>
    IVpnGatewayApplication Build();
}
