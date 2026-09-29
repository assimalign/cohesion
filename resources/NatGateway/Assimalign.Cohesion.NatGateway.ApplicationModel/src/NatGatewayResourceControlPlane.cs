using Assimalign.Cohesion.Hosting.Resources;

namespace Assimalign.Cohesion.ApplicationModel;

/// <summary>Creates the default control plane for enabled NatGateway resources.</summary>
public static class NatGatewayResourceControlPlane
{
    /// <summary>Creates a new isolated NatGateway resource control plane.</summary>
    /// <returns>The NatGateway area's default resource control plane.</returns>
    public static IResourceControlPlane Create() => ResourceControlPlane.Create();
}
