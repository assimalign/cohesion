namespace Assimalign.Cohesion.Fabric;

/// <summary>
/// Where a resource is in its lifecycle.
/// </summary>
public enum ProvisioningState
{
    /// <summary>
    /// The ResourceManager stored the resource; no reconcile has observed it yet.
    /// </summary>
    Accepted,

    /// <summary>
    /// A reconcile is converging the platform toward the spec.
    /// </summary>
    Provisioning,

    /// <summary>
    /// The platform matches the spec.
    /// </summary>
    Succeeded,

    /// <summary>
    /// The resource cannot converge without a change; its conditions and faults say why.
    /// </summary>
    Failed,

    /// <summary>
    /// The resource is being released before deletion.
    /// </summary>
    Deleting,
}
