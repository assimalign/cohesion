using Assimalign.Cohesion.Hosting.Resources;

namespace Assimalign.Cohesion.EmailHub.ApplicationModel;

/// <summary>Creates the default control plane for enabled EmailHub resources.</summary>
public static class EmailHubResourceControlPlane
{
    /// <summary>Creates a new isolated EmailHub resource control plane.</summary>
    /// <returns>The EmailHub area's default resource control plane.</returns>
    public static IResourceControlPlane Create() => ResourceControlPlane.Create();
}
