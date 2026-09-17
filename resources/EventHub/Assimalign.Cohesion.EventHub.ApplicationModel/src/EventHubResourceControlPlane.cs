using Assimalign.Cohesion.Hosting.Resources;

namespace Assimalign.Cohesion.EventHub.ApplicationModel;

/// <summary>Creates the default control plane for enabled EventHub resources.</summary>
public static class EventHubResourceControlPlane
{
    /// <summary>Creates a new isolated EventHub resource control plane.</summary>
    /// <returns>The EventHub area's default resource control plane.</returns>
    public static IResourceControlPlane Create() => ResourceControlPlane.Create();
}
