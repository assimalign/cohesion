using Assimalign.Cohesion.Hosting.Resources;

namespace Assimalign.Cohesion.SecretStore.ApplicationModel;

/// <summary>
/// Creates the default control plane shared by enabled secret-store resources.
/// </summary>
/// <remarks>
/// Secret-store hosting serves the transport-neutral plane and the store protocol under
/// the manifest's <c>/cohesion/v1</c> control-plane path on its <c>api</c> endpoint.
/// </remarks>
public static class SecretStoreResourceControlPlane
{
    private const string TrustGrantCommandKind = "cohesion.trust.add";

    /// <summary>
    /// Creates a new isolated secret-store resource control plane.
    /// </summary>
    /// <returns>The SecretStore area's default resource control plane.</returns>
    public static IResourceControlPlane Create()
    {
        return ResourceControlPlane.Create([TrustGrantCommandKind]);
    }
}
