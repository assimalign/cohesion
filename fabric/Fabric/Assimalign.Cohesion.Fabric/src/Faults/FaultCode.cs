namespace Assimalign.Cohesion.Fabric;

/// <summary>
/// Cohesion's closed, platform-neutral catalog of reconcile causes. A platform's
/// <see cref="IFabricFaultClassifier"/> maps its reasons onto these; it never adds a code.
/// </summary>
public enum FaultCode
{
    /// <summary>
    /// The platform reported a reason the classifier does not know. The platform detail is intact.
    /// </summary>
    PlatformError,

    /// <summary>
    /// The workload's image cannot be pulled: denied or unknown (needs a change), or throttled
    /// and unavailable (transient).
    /// </summary>
    ImageUnavailable,

    /// <summary>
    /// The workload starts and then exits, or an in-process entry point threw.
    /// </summary>
    WorkloadCrashing,

    /// <summary>
    /// The workload's configuration cannot be materialized, such as a missing setting or secret key.
    /// </summary>
    ConfigInvalid,

    /// <summary>
    /// The platform's policy refused the object: security admission or a webhook.
    /// </summary>
    PlatformForbidden,

    /// <summary>
    /// The platform's quota refused the object.
    /// </summary>
    QuotaExceeded,

    /// <summary>
    /// The platform has nowhere to place the workload.
    /// </summary>
    Unschedulable,

    /// <summary>
    /// The object belongs to another resource or another manager.
    /// </summary>
    OwnershipConflict,

    /// <summary>
    /// The platform's API failed or timed out.
    /// </summary>
    PlatformUnavailable,
}
