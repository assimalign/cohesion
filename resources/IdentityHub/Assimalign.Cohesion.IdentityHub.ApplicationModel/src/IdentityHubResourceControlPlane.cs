using Assimalign.Cohesion.Hosting.Resources;

namespace Assimalign.Cohesion.IdentityHub.ApplicationModel;

/// <summary>
/// Creates the default control plane shared by enabled identity-hub resources.
/// </summary>
public static class IdentityHubResourceControlPlane
{
    /// <summary>
    /// Creates a new isolated identity-hub resource control plane.
    /// </summary>
    /// <returns>The IdentityHub area's default resource control plane.</returns>
    /// <remarks>
    /// IdentityHub command kinds are intentionally empty until developer-experience item 31c.
    /// </remarks>
    public static IResourceControlPlane Create()
    {
        return ResourceControlPlane.Create();
    }
}
