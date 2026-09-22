using Assimalign.Cohesion.Hosting.Resources;

namespace Assimalign.Cohesion.ApplicationModel;

/// <summary>Creates the default control plane for enabled VpnGateway resources.</summary>
public static class VpnGatewayResourceControlPlane
{
    /// <summary>Creates a new isolated VpnGateway resource control plane.</summary>
    /// <returns>The VpnGateway area's default resource control plane.</returns>
    public static IResourceControlPlane Create() => ResourceControlPlane.Create();
}
