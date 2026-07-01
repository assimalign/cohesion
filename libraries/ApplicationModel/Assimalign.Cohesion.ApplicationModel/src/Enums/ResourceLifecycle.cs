namespace Assimalign.Cohesion.ApplicationModel;

/// <summary>
/// The observed lifecycle state of a resource as tracked by
/// <see cref="IApplicationResourceStateManager"/>.
/// </summary>
/// <remarks>
/// This is not an ordered lattice: readiness waits are membership tests over an explicit
/// terminal set, never "reached or passed" comparisons. <see cref="Blocked"/> and
/// <see cref="Skipped"/> mark dependents of a failed prerequisite.
/// </remarks>
public enum ResourceLifecycle
{
    /// <summary>Nothing has been observed yet.</summary>
    Unknown = 0,

    /// <summary>Declared but not yet acted on.</summary>
    Pending,

    /// <summary>The resource's artifact or image is being produced.</summary>
    Building,

    /// <summary>Platform objects are being applied.</summary>
    Provisioning,

    /// <summary>Started but not yet ready.</summary>
    Starting,

    /// <summary>Running and healthy.</summary>
    Running,

    /// <summary>Running but unhealthy.</summary>
    Degraded,

    /// <summary>Being stopped.</summary>
    Stopping,

    /// <summary>Stopped.</summary>
    Stopped,

    /// <summary>Failed to provision or start.</summary>
    Failed,

    /// <summary>Not started because a prerequisite failed.</summary>
    Blocked,

    /// <summary>Deliberately skipped because a prerequisite failed.</summary>
    Skipped
}
