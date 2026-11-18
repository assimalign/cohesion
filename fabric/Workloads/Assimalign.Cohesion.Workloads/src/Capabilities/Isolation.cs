namespace Assimalign.Cohesion.Workloads;

/// <summary>
/// How far apart a platform runs one workload from another.
/// </summary>
public enum Isolation
{
    /// <summary>
    /// Workloads share a process, as on the in-process platform.
    /// </summary>
    None,

    /// <summary>
    /// Each workload runs in its own process.
    /// </summary>
    Process,

    /// <summary>
    /// Each workload runs in its own container.
    /// </summary>
    Container,
}
