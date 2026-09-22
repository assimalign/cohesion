using Assimalign.Cohesion.Hosting.Resources;

namespace Assimalign.Cohesion.ApplicationModel;

/// <summary>
/// Creates the default control plane shared by enabled database resources.
/// </summary>
public static class DatabaseResourceControlPlane
{
    /// <summary>
    /// Creates a new isolated database resource control plane.
    /// </summary>
    /// <returns>The Database area's default resource control plane.</returns>
    public static IResourceControlPlane Create()
    {
        return ResourceControlPlane.Create(["database.add-database", "database.add-principal"]);
    }
}
