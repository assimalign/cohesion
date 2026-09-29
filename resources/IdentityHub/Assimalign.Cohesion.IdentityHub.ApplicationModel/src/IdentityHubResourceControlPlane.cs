using Assimalign.Cohesion.Hosting.Resources;

namespace Assimalign.Cohesion.ApplicationModel;

/// <summary>
/// Creates the default control plane shared by enabled identity-hub resources.
/// </summary>
public static class IdentityHubResourceControlPlane
{
    /// <summary>
    /// Creates a new isolated identity-hub resource control plane.
    /// </summary>
    /// <returns>The IdentityHub area's default resource control plane.</returns>
    public static IResourceControlPlane Create()
    {
        return ResourceControlPlane.Create(["identityhub.add-audience", "identityhub.add-client"]);
    }
}
