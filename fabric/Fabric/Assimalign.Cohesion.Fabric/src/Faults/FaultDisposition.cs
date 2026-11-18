namespace Assimalign.Cohesion.Fabric;

/// <summary>
/// What the reconciler does with a <see cref="FabricFault"/>.
/// </summary>
public enum FaultDisposition
{
    /// <summary>
    /// Requeue with backoff; the cause may clear by itself.
    /// </summary>
    Transient,

    /// <summary>
    /// Fail the operation at once; the resource cannot run without a change to what it runs.
    /// The reconciler keeps observing, so status can still turn ready at the same generation.
    /// </summary>
    NeedsChange,
}
