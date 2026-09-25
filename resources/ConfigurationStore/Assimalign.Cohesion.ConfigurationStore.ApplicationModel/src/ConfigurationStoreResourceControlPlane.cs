using Assimalign.Cohesion.Hosting.Resources;

namespace Assimalign.Cohesion.ApplicationModel;

/// <summary>
/// Creates the default control plane shared by enabled configuration-store resources.
/// </summary>
public static class ConfigurationStoreResourceControlPlane
{
    /// <summary>
    /// Creates a new isolated configuration-store resource control plane.
    /// </summary>
    /// <returns>The ConfigurationStore area's default resource control plane.</returns>
    public static IResourceControlPlane Create()
    {
        return ResourceControlPlane.Create(
        [
            "configurationstore.add-namespace",
            "configurationstore.set-value",
            "configurationstore.remove-value",
        ]);
    }
}
