using Assimalign.Cohesion.Hosting.Resources;

namespace Assimalign.Cohesion.Rezolvr.ApplicationModel;

/// <summary>Creates the default control plane for enabled Rezolvr resources.</summary>
public static class RezolvrResourceControlPlane
{
    /// <summary>Creates a new isolated Rezolvr resource control plane.</summary>
    /// <returns>The Rezolvr area's default resource control plane.</returns>
    public static IResourceControlPlane Create() => ResourceControlPlane.Create();
}
