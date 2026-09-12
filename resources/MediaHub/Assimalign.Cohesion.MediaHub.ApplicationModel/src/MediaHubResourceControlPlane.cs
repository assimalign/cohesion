using Assimalign.Cohesion.Hosting.Resources;

namespace Assimalign.Cohesion.MediaHub.ApplicationModel;

/// <summary>Creates the default control plane for enabled MediaHub resources.</summary>
public static class MediaHubResourceControlPlane
{
    /// <summary>Creates a new isolated MediaHub resource control plane.</summary>
    /// <returns>The MediaHub area's default resource control plane.</returns>
    public static IResourceControlPlane Create() => ResourceControlPlane.Create();
}
