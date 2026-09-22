using Assimalign.Cohesion.Hosting.Resources;

namespace Assimalign.Cohesion.ApplicationModel;

/// <summary>Creates the default control plane for enabled MessageHub resources.</summary>
public static class MessageHubResourceControlPlane
{
    /// <summary>Creates a new isolated MessageHub resource control plane.</summary>
    /// <returns>The MessageHub area's default resource control plane.</returns>
    public static IResourceControlPlane Create() => ResourceControlPlane.Create();
}
