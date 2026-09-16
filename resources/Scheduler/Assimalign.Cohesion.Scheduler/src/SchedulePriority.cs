namespace Assimalign.Cohesion.Scheduler;

/// <summary>
/// Identifies a schedule's compatibility priority classification.
/// </summary>
/// <remarks>
/// Scheduler providers own independent asynchronous loops, so this value does not guarantee
/// occurrence ordering or OS-thread affinity. A synchronous blocking workload that truly needs
/// a dedicated thread should be hosted as a dedicated-thread service by the area's
/// hosting module instead of an asynchronous schedule job.
/// </remarks>
public enum SchedulePriority
{
    /// <summary>
    /// Classifies a schedule as long-running without promising a dedicated execution thread.
    /// </summary>
    LongRunning,
    /// <summary>
    /// Classifies a schedule as high priority for providers that interpret priority metadata.
    /// </summary>
    High,
    /// <summary>
    /// Classifies a schedule as normal priority.
    /// </summary>
    Normal,
    /// <summary>
    /// Classifies a schedule as low priority for providers that interpret priority metadata.
    /// </summary>
    Low
}
