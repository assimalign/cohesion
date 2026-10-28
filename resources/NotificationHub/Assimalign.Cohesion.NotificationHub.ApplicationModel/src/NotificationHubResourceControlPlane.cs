using Assimalign.Cohesion.Hosting.Resources;

namespace Assimalign.Cohesion.ApplicationModel;

/// <summary>Creates the default control plane for enabled NotificationHub resources.</summary>
public static class NotificationHubResourceControlPlane
{
    /// <summary>Creates a new isolated NotificationHub resource control plane.</summary>
    /// <returns>The NotificationHub area's default resource control plane.</returns>
    public static IResourceControlPlane Create() => ResourceControlPlane.Create();
}
