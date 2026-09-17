using Assimalign.Cohesion.Hosting.Resources;

namespace Assimalign.Cohesion.LogSpace.ApplicationModel;

/// <summary>Creates the default control plane for enabled LogSpace resources.</summary>
public static class LogSpaceResourceControlPlane
{
    /// <summary>Creates a new isolated LogSpace resource control plane.</summary>
    /// <returns>The LogSpace area's default resource control plane.</returns>
    public static IResourceControlPlane Create() => ResourceControlPlane.Create();
}
