using Assimalign.Cohesion.Hosting;

namespace Assimalign.Cohesion.Web.ApplicationModel;

/// <summary>
/// Creates the default control plane shared by enabled Web resources.
/// </summary>
public static class WebResourceControlPlane
{
    /// <summary>
    /// Creates a new isolated Web resource control plane.
    /// </summary>
    /// <returns>The Web area's default resource control plane.</returns>
    public static IResourceControlPlane Create()
    {
        return ResourceControlPlane.Create();
    }
}
