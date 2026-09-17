using Assimalign.Cohesion.Hosting.Resources;

namespace Assimalign.Cohesion.IoTHub.ApplicationModel;

/// <summary>Creates the default control plane for enabled IoTHub resources.</summary>
public static class IoTHubResourceControlPlane
{
    /// <summary>Creates a new isolated IoTHub resource control plane.</summary>
    /// <returns>The IoTHub area's default resource control plane.</returns>
    public static IResourceControlPlane Create() => ResourceControlPlane.Create();
}
