using Assimalign.Cohesion.Hosting.Resources;

namespace Assimalign.Cohesion.ApplicationModel;

/// <summary>Creates the default control plane for enabled ApiManager resources.</summary>
public static class ApiManagerResourceControlPlane
{
    /// <summary>Creates a new isolated ApiManager resource control plane.</summary>
    /// <returns>The ApiManager area's default resource control plane.</returns>
    public static IResourceControlPlane Create() => ResourceControlPlane.Create();
}
