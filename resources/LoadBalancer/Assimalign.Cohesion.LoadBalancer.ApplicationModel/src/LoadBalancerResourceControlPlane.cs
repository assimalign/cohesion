using Assimalign.Cohesion.Hosting.Resources;

namespace Assimalign.Cohesion.ApplicationModel;

/// <summary>Creates the default control plane for enabled LoadBalancer resources.</summary>
public static class LoadBalancerResourceControlPlane
{
    /// <summary>Creates a new isolated LoadBalancer resource control plane.</summary>
    /// <returns>The LoadBalancer area's default resource control plane.</returns>
    public static IResourceControlPlane Create() => ResourceControlPlane.Create();
}
